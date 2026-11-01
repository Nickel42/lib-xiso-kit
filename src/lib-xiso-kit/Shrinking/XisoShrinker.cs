using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using LibXisoKit.Analysis;
using LibXisoKit.Identity;
using LibXisoKit.IO;
using LibXisoKit.Xbe;
using LibXisoKit.Xdvdfs;

namespace LibXisoKit.Shrinking;

/// <summary>
/// High-level entry point: open → analyze → plan → (user reviews) → execute → verify.
/// One instance holds the source image open; dispose it when done.
/// Instances are not thread-safe; run one operation at a time per instance.
/// </summary>
/// <example>
/// <code>
/// using var shrinker = XisoShrinker.Open(@"D:\Redump\Halo.iso");
/// var analysis = await shrinker.AnalyzeAsync(new AnalyzeOptions { FindDuplicates = true });
/// var plan = shrinker.CreatePlan(analysis, new ShrinkOptions { Deduplicate = true }, rules);
/// // show plan.Items, let the user toggle .Remove
/// var result = await shrinker.ExecuteAsync(plan, @"D:\XISO\Halo.iso", progress, token);
/// </code>
/// </example>
public sealed class XisoShrinker : IDisposable
{
    private XisoShrinker(string path, XdvdfsVolume volume)
    {
        InputPath = Path.GetFullPath(path);
        Volume = volume;
    }

    public string InputPath { get; }

    public XdvdfsVolume Volume { get; }

    /// <summary>Opens an image (Redump ISO, XISO, split ISO, CCI or CSO).</summary>
    public static XisoShrinker Open(string path) => new(path, XdvdfsVolume.Open(path));

    public static Task<XisoShrinker> OpenAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Open(path), cancellationToken);

    public IsoAnalysis Analyze(AnalyzeOptions? options = null, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        IsoAnalyzer.Analyze(Volume, options, progress, cancellationToken);

    public Task<IsoAnalysis> AnalyzeAsync(AnalyzeOptions? options = null, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Analyze(options, progress, cancellationToken), cancellationToken);

    /// <summary>
    /// Builds a plan from built-in rules, an optional rules database, heuristics and <see cref="ShrinkOptions.ExcludePatterns"/>.
    /// Nothing is read or written.
    /// </summary>
    public ShrinkPlan CreatePlan(IsoAnalysis analysis, ShrinkOptions options, ShrinkRuleSet? rules = null)
    {
        var ruleSet = rules is null ? ShrinkRuleSet.BuiltIn : ShrinkRuleSet.Merge(ShrinkRuleSet.BuiltIn, rules);
        var items = new List<PlannedRemoval>();
        var warnings = new List<string>(analysis.Warnings);
        var byPath = new Dictionary<string, PlannedRemoval>(StringComparer.OrdinalIgnoreCase);

        void Propose(XdvdfsEntry entry, RemovalSource source, Confidence confidence, string reason, bool remove)
        {
            if (byPath.TryGetValue(entry.Path, out var existing))
            {
                // Keep the strongest claim; a later "remove" never un-selects.
                if (remove && !existing.Remove)
                {
                    existing.Remove = true;
                }
                return;
            }
            var (bytes, files) = ShrinkPlan.Measure(entry);
            var item = new PlannedRemoval(entry.Path, entry.IsDirectory, bytes, files, source, confidence, reason, remove);
            items.Add(item);
            byPath[entry.Path] = item;
        }

        // 1. Rules (built-in + database).
        uint? titleId = analysis.DefaultXbe?.TitleId;
        uint? version = analysis.DefaultXbe?.Version;
        bool builtInsDone = false;
        foreach (var (rule, title) in ruleSet.RulesFor(titleId, version))
        {
            bool isBuiltIn = !builtInsDone && title is null && rule.Remove.Contains("**/$SystemUpdate/**");
            builtInsDone |= isBuiltIn;
            var matcher = new GlobMatcher(rule.Remove);
            RemovalSource source = isBuiltIn ? RemovalSource.BuiltIn : rule.Verified ? RemovalSource.VerifiedRule : RemovalSource.UnverifiedRule;
            bool remove = isBuiltIn ? options.RemoveSystemUpdate : rule.Verified || options.ApplyUnverifiedRules;
            string reason = rule.Reason ?? (title?.Name is { } n ? $"Rule for {n}." : "Rule match.");
            foreach (var entry in MatchTopmost(matcher))
            {
                Propose(entry, source, rule.Verified ? Confidence.Safe : Confidence.Likely, reason, remove);
            }
        }

        // 2. Heuristic findings.
        foreach (var f in analysis.Findings)
        {
            if (f.Kind != FindingKind.DemoContent)
            {
                continue;
            }
            var entry = Volume.Find(f.Path);
            if (entry is not null)
            {
                Propose(entry, RemovalSource.Heuristic, f.Confidence, f.Message,
                    options.RemoveLikelyDemos && f.Confidence <= Confidence.Likely);
            }
        }

        // 3. Caller patterns.
        if (options.ExcludePatterns.Count > 0)
        {
            var matcher = new GlobMatcher(options.ExcludePatterns);
            foreach (var entry in MatchTopmost(matcher))
            {
                Propose(entry, RemovalSource.User, Confidence.Review, $"Matches {matcher.FirstMatch(entry.Path)}.", true);
            }
        }

        if (options.Mode == ShrinkMode.TrimOnly && (items.Any(i => i.Remove) || options.Deduplicate))
        {
            warnings.Add("Trim-only mode keeps every file; selected removals and deduplication need Rebuild mode.");
        }
        if (options.Deduplicate && analysis.Duplicates.Count == 0)
        {
            warnings.Add("Run the analysis with FindDuplicates to see deduplication savings in advance.");
        }
        if (analysis.DefaultXbe is null)
        {
            warnings.Add("No readable /default.xbe; output may not boot.");
        }

        var removesDefault = items.Any(i => string.Equals(i.Path, "/default.xbe", StringComparison.OrdinalIgnoreCase));
        if (removesDefault)
        {
            warnings.Add("The plan touches /default.xbe; it will always be kept.");
            items.RemoveAll(i => string.Equals(i.Path, "/default.xbe", StringComparison.OrdinalIgnoreCase));
        }

        return new ShrinkPlan(Volume, analysis, options, items, warnings);
    }

    /// <summary>Entries matched by <paramref name="matcher"/>, skipping descendants of matched directories.</summary>
    private IEnumerable<XdvdfsEntry> MatchTopmost(GlobMatcher matcher)
    {
        if (matcher.IsEmpty)
        {
            yield break;
        }
        var stack = new Stack<XdvdfsEntry>(Volume.Root.Children.Reverse());
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (matcher.IsMatch(e.Path))
            {
                yield return e;
                continue;
            }
            for (int i = e.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(e.Children[i]);
            }
        }
    }

    public Task<ShrinkResult> ExecuteAsync(ShrinkPlan plan, string outputPath, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Execute(plan, outputPath, progress, cancellationToken), cancellationToken);

    /// <summary>Produces the output described by <paramref name="plan"/>. The source is never modified (unless deletion is requested).</summary>
    public ShrinkResult Execute(ShrinkPlan plan, string outputPath, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (plan.Volume != Volume)
        {
            throw new ArgumentException("The plan was created by a different shrinker instance.", nameof(plan));
        }
        var options = plan.Options;
        var sw = Stopwatch.StartNew();
        string fullOutput = Path.GetFullPath(outputPath);
        foreach (var f in Volume.Source.Files)
        {
            if (string.Equals(Path.GetFullPath(f), fullOutput, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The output path must be different from the input.");
            }
        }

        var mode = options.Mode;
        if (mode == ShrinkMode.TrimOnly && plan.RequiresRebuild)
        {
            throw new InvalidOperationException("Trim-only mode cannot remove or deduplicate files. Use ShrinkMode.Rebuild.");
        }

        // 1. Identity of the untouched source.
        FileHashes? hashes = null;
        DatEntry? datMatch = null;
        if (options.HashSource || options.Dat is not null)
        {
            hashes = FileHashes.Compute(Volume.Source.Files, progress, cancellationToken);
            datMatch = options.Dat?.Match(hashes);
        }

        // 2. Lay out the new image.
        progress?.Report(new OperationProgress(OperationStage.Analyzing, 0, 0));
        ImageSource image;
        var removed = plan.SelectedEntries().ToList();
        int dupFiles = 0;
        long dupBytes = 0;
        int patched;
        long scrubbed = 0;
        int expectedFiles;

        if (mode == ShrinkMode.Rebuild)
        {
            var root = BuildTree(new HashSet<string>(plan.SelectedPaths, StringComparer.OrdinalIgnoreCase));
            var build = XisoBuilder.Build(root, new XisoBuildOptions
            {
                Deduplicate = options.Deduplicate,
                MediaPatch = options.MediaPatch,
                FileTime = options.Timestamp switch
                {
                    TimestampMode.Deterministic => 0UL,
                    TimestampMode.Now => (ulong)DateTime.UtcNow.ToFileTimeUtc(),
                    _ => Volume.FileTimeRaw,
                },
            }, progress, cancellationToken);
            image = build.Image;
            dupFiles = build.DuplicateFiles;
            dupBytes = build.DuplicateBytesSaved;
            patched = build.PatchedExecutables;
            expectedFiles = build.FileCount;
        }
        else
        {
            var trim = PartitionTrimmer.Build(Volume, options.Scrub, options.MediaPatch);
            image = trim.Image;
            patched = trim.PatchedExecutables;
            scrubbed = trim.ScrubbedBytes;
            expectedFiles = Volume.FileCount;
        }

        // 3. Write.
        var write = ImageWriter.Write(image, fullOutput, options.Format, options.SplitSize, options.Overwrite, progress, cancellationToken);

        try
        {
            // 4. Verify.
            VerifyResult? verify = null;
            if (options.Verify)
            {
                verify = VerifyImage(write.Files[0], write.LogicalSha1, expectedFiles, progress, cancellationToken);
                if (!verify.Success)
                {
                    throw new InvalidDataException("Verification of the written image failed: " + string.Join("; ", verify.Problems));
                }
            }

            long sourceBytes = Volume.Source.Files.Sum(f => new FileInfo(f).Length);
            long outputBytes = write.Files.Sum(f => new FileInfo(f).Length);
            var removedFiles = removed.Where(e => !e.IsDirectory).ToList();

            // 5. Sidecar.
            string? sidecarPath = null;
            if (options.WriteSidecar)
            {
                var xbe = plan.Analysis.DefaultXbe;
                var sidecar = new ShrinkSidecar
                {
                    Source = new SidecarSource
                    {
                        Files = Volume.Source.Files.Select(f => Path.GetFileName(f) ?? string.Empty).ToList(),
                        Size = sourceBytes,
                        Container = Volume.Source.Format.ToString(),
                        Layout = Volume.Layout.ToString(),
                        Crc32 = hashes?.Crc32,
                        Md5 = hashes?.Md5,
                        Sha1 = hashes?.Sha1,
                        DatName = datMatch is null ? null : options.Dat?.Name,
                        DatGame = datMatch?.GameName,
                        DatRom = datMatch?.RomName,
                    },
                    Title = xbe is null ? null : new SidecarTitle
                    {
                        TitleId = xbe.TitleIdHex,
                        TitleIdDisplay = xbe.TitleIdDisplay,
                        Name = xbe.TitleName,
                        Region = xbe.RegionLabel,
                        Version = xbe.Version,
                        DiscNumber = xbe.DiscNumber,
                    },
                    Changes = new SidecarChanges
                    {
                        Mode = mode.ToString(),
                        RemovedPaths = plan.SelectedPaths.ToList(),
                        RemovedFiles = removedFiles.Count,
                        RemovedBytes = removedFiles.Sum(e => (long)e.Size),
                        DeduplicatedFiles = dupFiles,
                        DeduplicatedBytes = dupBytes,
                        MediaPatchedExecutables = patched,
                        ScrubbedBytes = scrubbed,
                        Timestamp = options.Timestamp.ToString(),
                    },
                    Output = new SidecarOutput
                    {
                        Files = write.Files.Select(f => Path.GetFileName(f) ?? string.Empty).ToList(),
                        Format = options.Format.ToString(),
                        SizeOnDisk = outputBytes,
                        LogicalSize = write.LogicalLength,
                        LogicalSha1 = write.LogicalSha1,
                        Verified = verify?.Success,
                    },
                };
                sidecarPath = ShrinkSidecar.PathFor(fullOutput);
                File.WriteAllText(sidecarPath, sidecar.ToJson());
            }

            // 6. Optional source deletion (only after a successful verification).
            bool deleted = false;
            if (options.DeleteSourceAfterVerify)
            {
                if (verify is not { Success: true })
                {
                    throw new InvalidOperationException("Refusing to delete the source without a successful verification.");
                }
                var files = Volume.Source.Files.ToArray();
                Volume.Dispose();
                foreach (var f in files)
                {
                    File.Delete(f);
                }
                deleted = true;
            }

            progress?.Report(new OperationProgress(OperationStage.Finished, 1, 1));
            return new ShrinkResult
            {
                OutputFiles = write.Files,
                Format = options.Format,
                Mode = mode,
                SourceBytes = sourceBytes,
                OutputBytes = outputBytes,
                LogicalBytes = write.LogicalLength,
                LogicalSha1 = write.LogicalSha1,
                RemovedPaths = plan.SelectedPaths,
                RemovedFileCount = removedFiles.Count,
                RemovedBytes = removedFiles.Sum(e => (long)e.Size),
                DeduplicatedFiles = dupFiles,
                DeduplicatedBytes = dupBytes,
                PatchedExecutables = patched,
                ScrubbedBytes = scrubbed,
                Verification = verify,
                SourceHashes = hashes,
                DatMatch = datMatch,
                SidecarPath = sidecarPath,
                SourceDeleted = deleted,
                Elapsed = sw.Elapsed,
            };
        }
        catch
        {
            // Don't leave a half-trusted output behind.
            foreach (var f in write.Files)
            {
                TryDelete(f);
            }
            TryDelete(ShrinkSidecar.PathFor(fullOutput));
            throw;
        }
    }

    private XisoBuildNode BuildTree(HashSet<string> removedPaths)
    {
        var root = XisoBuildNode.Directory(string.Empty, "/");
        var stack = new Stack<(XdvdfsEntry Source, XisoBuildNode Target)>();
        stack.Push((Volume.Root, root));
        while (stack.Count > 0)
        {
            var (src, dst) = stack.Pop();
            foreach (var c in src.Children)
            {
                if (removedPaths.Contains(c.Path) && !string.Equals(c.Path, "/default.xbe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (c.IsDirectory)
                {
                    var d = dst.Add(XisoBuildNode.Directory(c.Name, c.Path));
                    stack.Push((c, d));
                }
                else
                {
                    var content = ContentProvider.FromSource(Volume.Source, Volume.PartitionOffset + (long)c.StartSector * XdvdfsConstants.SectorSize);
                    dst.Add(XisoBuildNode.File(c.Name, c.Size, content, c.Path));
                }
            }
        }
        return root;
    }

    /// <summary>
    /// Re-opens an image and checks it: filesystem walk, /default.xbe parse, optional file count and
    /// optional SHA-1 of the whole logical (decompressed) image.
    /// </summary>
    public static VerifyResult VerifyImage(
        string path,
        string? expectedLogicalSha1 = null,
        int? expectedFileCount = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool hashImage = false)
    {
        var problems = new List<string>();
        string? sha1 = null;
        int fileCount = 0;
        XbeInfo? xbe = null;
        try
        {
            using var volume = XdvdfsVolume.Open(path);
            fileCount = volume.FileCount;
            problems.AddRange(volume.Warnings);
            if (expectedFileCount is { } expected && expected != fileCount)
            {
                problems.Add($"Expected {expected} files but found {fileCount}.");
            }

            var defaultXbe = volume.Find("/default.xbe");
            if (defaultXbe is { IsDirectory: false, Size: > 0 })
            {
                var head = new byte[Math.Min(defaultXbe.Size, 256 * 1024)];
                int n = volume.ReadFile(defaultXbe, 0, head);
                xbe = XbeReader.TryParse(head.AsSpan(0, n));
            }
            if (xbe is null)
            {
                problems.Add("/default.xbe is missing or invalid.");
            }

            if (expectedLogicalSha1 is not null || hashImage)
            {
                var throttled = new ThrottledProgress(progress);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(4 << 20);
                try
                {
                    long pos = 0;
                    long len = volume.Source.Length;
                    while (pos < len)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int want = (int)Math.Min(buffer.Length, len - pos);
                        volume.Source.ReadExactly(pos, buffer.AsSpan(0, want));
                        hash.AppendData(buffer, 0, want);
                        pos += want;
                        throttled.Report(new OperationProgress(OperationStage.Verifying, pos, len));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                sha1 = Convert.ToHexString(hash.GetHashAndReset());
                if (expectedLogicalSha1 is not null && !string.Equals(sha1, expectedLogicalSha1, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add("Image contents differ from what was written (SHA-1 mismatch).");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            problems.Add(ex.Message);
        }

        return new VerifyResult(problems.Count == 0, sha1, fileCount, xbe, problems);
    }

    /// <summary>One-call convenience: open, analyze, plan with defaults and rules, execute.</summary>
    public static async Task<ShrinkResult> ShrinkAsync(
        string inputPath,
        string outputPath,
        ShrinkOptions? options = null,
        ShrinkRuleSet? rules = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ShrinkOptions();
        using var shrinker = await OpenAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var analysis = await shrinker.AnalyzeAsync(new AnalyzeOptions(), progress, cancellationToken).ConfigureAwait(false);
        var plan = shrinker.CreatePlan(analysis, options, rules);
        return await shrinker.ExecuteAsync(plan, outputPath, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Packs a folder (e.g. an extracted game) into a new XISO/CCI/CSO.</summary>
    public static WriteResult PackFolder(
        string folder,
        string outputPath,
        OutputFormat format = OutputFormat.Iso,
        XisoBuildOptions? buildOptions = null,
        long? splitSize = null,
        bool overwrite = false,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = XisoBuildNode.FromDirectory(folder);
        var build = XisoBuilder.Build(root, buildOptions ?? new XisoBuildOptions { FileTime = (ulong)DateTime.UtcNow.ToFileTimeUtc() }, progress, cancellationToken);
        return ImageWriter.Write(build.Image, outputPath, format, splitSize, overwrite, progress, cancellationToken);
    }

    /// <summary>Converts any supported image to another container without changing its contents (e.g. ISO → CCI).</summary>
    public static WriteResult ConvertImage(
        string inputPath,
        string outputPath,
        OutputFormat format,
        long? splitSize = null,
        bool overwrite = false,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var source = ImageOpener.Open(inputPath);
        return ImageWriter.Write(source, outputPath, format, splitSize, overwrite, progress, cancellationToken);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => Volume.Dispose();
}
