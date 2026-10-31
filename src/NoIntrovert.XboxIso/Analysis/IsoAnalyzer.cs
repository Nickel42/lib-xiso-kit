using System.Security.Cryptography;
using NoIntrovert.XboxIso.IO;
using NoIntrovert.XboxIso.Xbe;
using NoIntrovert.XboxIso.Xdvdfs;

namespace NoIntrovert.XboxIso.Analysis;

public enum FindingKind
{
    /// <summary>Bundled dashboard update ($SystemUpdate).</summary>
    SystemUpdate,
    /// <summary>Folder that looks like bundled demos, trailers or bonus content.</summary>
    DemoContent,
    /// <summary>An executable other than /default.xbe.</summary>
    SecondaryExecutable,
    /// <summary>Large video file outside a demo folder (usually required by the game).</summary>
    Video,
    /// <summary>Byte-identical copies of the same file.</summary>
    Duplicate,
    /// <summary>Matched by a rules database entry.</summary>
    Rule,
}

public enum Confidence
{
    /// <summary>Removal is known to be safe.</summary>
    Safe,
    /// <summary>Very likely removable, but unverified for this title.</summary>
    Likely,
    /// <summary>Possibly removable; needs a human to look.</summary>
    Review,
    /// <summary>Informational only; do not remove.</summary>
    Info,
}

/// <summary>Something the analyzer noticed about a path.</summary>
public sealed record AnalysisFinding(string Path, long Bytes, FindingKind Kind, Confidence Confidence, string Message);

/// <summary>A set of identical files.</summary>
public sealed record DuplicateGroup(long Size, string Sha256, IReadOnlyList<string> Paths)
{
    /// <summary>Bytes saved if every copy shares one set of sectors.</summary>
    public long ReclaimableBytes => Size * (Paths.Count - 1);
}

/// <summary>Options for <see cref="IsoAnalyzer"/>.</summary>
public sealed record AnalyzeOptions
{
    /// <summary>Hash files to find duplicates (reads every file whose size is shared by another file).</summary>
    public bool FindDuplicates { get; init; }

    /// <summary>Extra folder names treated as demo/bonus content (case-insensitive).</summary>
    public IReadOnlyList<string> ExtraDemoFolderNames { get; init; } = Array.Empty<string>();
}

/// <summary>Everything known about an image before shrinking it.</summary>
public sealed class IsoAnalysis
{
    public required string ImagePath { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public required ContainerFormat Container { get; init; }
    public required DiscLayout Layout { get; init; }
    public required long ImageLength { get; init; }
    public required long PartitionOffset { get; init; }
    public required long PartitionLength { get; init; }
    public required long UsedBytes { get; init; }
    public required long FileBytes { get; init; }
    public required long DirectoryTableBytes { get; init; }
    public required int FileCount { get; init; }
    public required int DirectoryCount { get; init; }
    public required bool IsOptimized { get; init; }
    public required XbeInfo? DefaultXbe { get; init; }
    public required IReadOnlyList<AnalysisFinding> Findings { get; init; }
    public required IReadOnlyList<DuplicateGroup> Duplicates { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required ulong FileTimeRaw { get; init; }

    /// <summary>Bytes before the game partition (the video partition on Redump images).</summary>
    public long VideoPartitionBytes => PartitionOffset;

    /// <summary>Bytes after the last used sector of the game partition.</summary>
    public long TrailingUnusedBytes => Math.Max(0, PartitionLength - UsedBytes);

    /// <summary>Unused sectors between files (filler) that scrubbing would zero.</summary>
    public long InteriorGapBytes =>
        Math.Max(0, UsedBytes - FileSectorBytes - DirectoryTableBytes - (XdvdfsConstants.DescriptorSector + 1) * (long)XdvdfsConstants.SectorSize);

    internal long FileSectorBytes { get; init; }

    public long DuplicateBytes => Duplicates.Sum(d => d.ReclaimableBytes);
}

/// <summary>Inspects an image and reports what could be removed.</summary>
public static class IsoAnalyzer
{
    private static readonly HashSet<string> DemoFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "demo", "demos", "xdemos", "xdemo", "trailer", "trailers", "preview", "previews",
        "promo", "promos", "bonus", "extras", "sneakpeek", "sneakpeeks", "sneak_peek", "sneak_peeks",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xmv", ".wmv", ".bik", ".sfd", ".mpg", ".mpeg", ".m2v", ".vid",
    };

    public static IsoAnalysis Analyze(
        XdvdfsVolume volume,
        AnalyzeOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AnalyzeOptions();
        var demoNames = new HashSet<string>(DemoFolderNames, StringComparer.OrdinalIgnoreCase);
        foreach (var n in options.ExtraDemoFolderNames)
        {
            demoNames.Add(n);
        }

        var findings = new List<AnalysisFinding>();
        long fileSectorBytes = 0;
        long tableBytes = volume.RootSize == 0 ? 0 : XdvdfsConstants.SectorsFor(volume.RootSize) * XdvdfsConstants.SectorSize;
        var countedSectors = new HashSet<uint>();

        foreach (var e in volume.EnumerateEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (e.IsDirectory)
            {
                tableBytes += e.SectorCount * XdvdfsConstants.SectorSize;
                if (e.Name.Equals("$SystemUpdate", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new AnalysisFinding(e.Path, SizeOfTree(e), FindingKind.SystemUpdate, Confidence.Safe,
                        "Dashboard update bundled on the disc; not needed to play."));
                }
                else if (demoNames.Contains(e.Name) && !IsUnderSystemUpdate(e))
                {
                    bool hasXbe = Descendants(e).Any(d => !d.IsDirectory && XbeMediaPatch.AppliesTo(d.Name));
                    findings.Add(new AnalysisFinding(e.Path, SizeOfTree(e), FindingKind.DemoContent,
                        hasXbe ? Confidence.Likely : Confidence.Review,
                        hasXbe
                            ? "Folder name and executables suggest bundled playable demos. Check the game's menus don't link to it."
                            : "Folder name suggests trailers or bonus content. Check the game doesn't play these itself."));
                }
                continue;
            }

            if (e.Size > 0 && countedSectors.Add(e.StartSector))
            {
                fileSectorBytes += e.SectorCount * XdvdfsConstants.SectorSize;
            }

            if (IsUnderSystemUpdate(e) || IsUnderDemo(e, demoNames))
            {
                continue;
            }

            string ext = System.IO.Path.GetExtension(e.Name);
            if (XbeMediaPatch.AppliesTo(e.Name) && !e.Path.Equals("/default.xbe", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new AnalysisFinding(e.Path, e.Size, FindingKind.SecondaryExecutable, Confidence.Info,
                    "Additional executable. Some games launch these (multiplayer, extras); keep unless a verified rule says otherwise."));
            }
            else if (VideoExtensions.Contains(ext) && e.Size >= 1024 * 1024)
            {
                findings.Add(new AnalysisFinding(e.Path, e.Size, FindingKind.Video, Confidence.Info,
                    "Video file. Intros and cutscenes are usually required; removing them can crash the game."));
            }
        }

        XbeInfo? xbe = null;
        var defaultXbe = volume.Find("/default.xbe");
        if (defaultXbe is { IsDirectory: false, Size: > 0 })
        {
            var head = new byte[Math.Min(defaultXbe.Size, 256 * 1024)];
            int n = volume.ReadFile(defaultXbe, 0, head);
            xbe = XbeReader.TryParse(head.AsSpan(0, n));
        }

        var duplicates = options.FindDuplicates
            ? FindDuplicates(volume, progress, cancellationToken)
            : (IReadOnlyList<DuplicateGroup>)Array.Empty<DuplicateGroup>();
        foreach (var g in duplicates)
        {
            findings.Add(new AnalysisFinding(g.Paths[0], g.ReclaimableBytes, FindingKind.Duplicate, Confidence.Safe,
                $"{g.Paths.Count} identical copies; deduplication shares one copy without removing any file."));
        }

        var warnings = new List<string>(volume.Warnings);
        if (xbe is null)
        {
            warnings.Add("/default.xbe is missing or unreadable; this may not be a game disc.");
        }

        return new IsoAnalysis
        {
            ImagePath = volume.Source.Files.Count > 0 ? volume.Source.Files[0] : string.Empty,
            Files = volume.Source.Files,
            Container = volume.Source.Format,
            Layout = volume.Layout,
            ImageLength = volume.Source.Length,
            PartitionOffset = volume.PartitionOffset,
            PartitionLength = volume.PartitionLength,
            UsedBytes = volume.UsedEndSector * XdvdfsConstants.SectorSize,
            FileBytes = volume.TotalFileBytes,
            DirectoryTableBytes = tableBytes,
            FileSectorBytes = fileSectorBytes,
            FileCount = volume.FileCount,
            DirectoryCount = volume.DirectoryCount,
            IsOptimized = volume.IsOptimized,
            DefaultXbe = xbe,
            Findings = findings,
            Duplicates = duplicates,
            Warnings = warnings,
            FileTimeRaw = volume.FileTimeRaw,
        };
    }

    /// <summary>Finds byte-identical files. Only files that share a size with another file are read.</summary>
    public static IReadOnlyList<DuplicateGroup> FindDuplicates(XdvdfsVolume volume, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var bySize = volume.EnumerateFiles()
            .Where(f => f.Size > 0)
            .GroupBy(f => f.Size)
            .Where(g => g.Select(f => f.StartSector).Distinct().Count() > 1)
            .ToList();

        long total = bySize.Sum(g => g.Sum(f => (long)f.Size));
        long done = 0;
        var throttled = new ThrottledProgress(progress);
        var groups = new List<DuplicateGroup>();
        var buffer = new byte[1 << 20];

        foreach (var sizeGroup in bySize)
        {
            var byHash = new Dictionary<string, List<XdvdfsEntry>>();
            var hashedSectors = new Dictionary<uint, string>();
            foreach (var f in sizeGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!hashedSectors.TryGetValue(f.StartSector, out var hash))
                {
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long offset = 0;
                    while (offset < f.Size)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int n = volume.ReadFile(f, offset, buffer);
                        if (n <= 0)
                        {
                            throw new EndOfStreamException($"{f.Path} is truncated.");
                        }
                        sha.AppendData(buffer, 0, n);
                        offset += n;
                        done += n;
                        throttled.Report(new OperationProgress(OperationStage.Hashing, done, total, f.Path));
                    }
                    hash = Convert.ToHexString(sha.GetHashAndReset());
                    hashedSectors[f.StartSector] = hash;
                }
                if (!byHash.TryGetValue(hash, out var list))
                {
                    byHash[hash] = list = new List<XdvdfsEntry>();
                }
                list.Add(f);
            }

            foreach (var (hash, list) in byHash)
            {
                // Files already sharing sectors (cross-linked) don't cost extra space.
                var distinct = list.GroupBy(f => f.StartSector).Select(g => g.First()).ToList();
                if (distinct.Count > 1)
                {
                    groups.Add(new DuplicateGroup(sizeGroup.Key, hash, list.Select(f => f.Path).ToList()));
                }
            }
        }
        return groups;
    }

    private static long SizeOfTree(XdvdfsEntry dir) =>
        Descendants(dir).Where(d => !d.IsDirectory).Sum(d => (long)d.Size);

    private static IEnumerable<XdvdfsEntry> Descendants(XdvdfsEntry dir)
    {
        foreach (var c in dir.Children)
        {
            yield return c;
            foreach (var d in Descendants(c))
            {
                yield return d;
            }
        }
    }

    private static bool IsUnderSystemUpdate(XdvdfsEntry e)
    {
        for (var p = e.Parent; p is not null; p = p.Parent)
        {
            if (p.Name.Equals("$SystemUpdate", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsUnderDemo(XdvdfsEntry e, HashSet<string> demoNames)
    {
        for (var p = e.Parent; p is not null; p = p.Parent)
        {
            if (demoNames.Contains(p.Name))
            {
                return true;
            }
        }
        return false;
    }
}
