using System.Diagnostics;
using LibXisoKit;
using LibXisoKit.Analysis;
using LibXisoKit.Cli;
using LibXisoKit.Identity;
using LibXisoKit.IO;
using LibXisoKit.Shrinking;
using LibXisoKit.Xdvdfs;

return await CliApp.RunAsync(args);

namespace LibXisoKit.Cli
{
    internal static class CliApp
    {
        private const string Usage = """
            xiso-kit - shrink, convert and verify original Xbox disc images (lib-xiso-kit)

            Usage:
              xiso-kit info <image>
              xiso-kit list <image>
              xiso-kit analyze <image> [--dupes] [--rules <file>]...
              xiso-kit shrink <input> <output> [options]
              xiso-kit verify <image>
              xiso-kit convert <input> <output> [--format iso|cci|cso] [--split] [--overwrite]
              xiso-kit pack <folder> <output> [--format iso|cci|cso] [--split] [--no-media-patch] [--overwrite]
              xiso-kit extract <image> <folder>
              xiso-kit hash <image> [--dat <file>]
              xiso-kit selftest [--keep]

            Shrink options:
              --mode rebuild|trim      rebuild (default) repacks; trim keeps the original layout
              --format iso|cci|cso     output container (default iso)
              --split                  split into FATX-sized parts (<4 GiB)
              --split-size <bytes>     split into parts of this size (multiple of 2048)
              --dedupe                 share sectors between identical files
              --exclude <glob>         remove matching paths (repeatable)
              --rules <file>           rules database JSON (repeatable)
              --apply-unverified       apply rules not marked verified
              --remove-likely-demos    remove folders the heuristics rate "likely demo"
              --keep-systemupdate      keep $SystemUpdate
              --no-media-patch         leave .xbe files byte-for-byte unchanged
              --no-scrub               trim mode: keep original filler data
              --deterministic          write a zero timestamp (reproducible output)
              --no-verify              skip re-reading the output
              --no-sidecar             don't write <output>.shrink.json
              --hash                   hash the source for the sidecar
              --dat <file>             identify the source against a Redump DAT
              --overwrite              replace existing output
              --dry-run                show the plan only
            """;

        public static async Task<int> RunAsync(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }

            var a = new Args(args.Skip(1));
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "info": return Info(a.Positional(0));
                    case "list": return List(a.Positional(0));
                    case "analyze": return Analyze(a);
                    case "shrink": return await Shrink(a, cts.Token);
                    case "verify": return Verify(a.Positional(0), cts.Token);
                    case "convert": return ConvertCmd(a, cts.Token);
                    case "pack": return Pack(a, cts.Token);
                    case "extract": return Extract(a, cts.Token);
                    case "hash": return Hash(a, cts.Token);
                    case "selftest": return SelfTest.Run(a.Has("--keep"));
                    default:
                        Console.Error.WriteLine($"Unknown command '{args[0]}'.\n");
                        Console.WriteLine(Usage);
                        return 1;
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("\nCancelled.");
                return 130;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                Console.Error.WriteLine($"\nError: {ex.Message}");
                return 2;
            }
        }

        private static int Info(string path)
        {
            using var shrinker = XisoShrinker.Open(path);
            var an = shrinker.Analyze();
            PrintSummary(an);
            return 0;
        }

        private static void PrintSummary(IsoAnalysis an)
        {
            Console.WriteLine($"Image:        {string.Join(", ", an.Files.Select(f => Path.GetFileName(f)))}");
            Console.WriteLine($"Container:    {an.Container}   Layout: {an.Layout}   Optimized tag: {(an.IsOptimized ? "yes" : "no")}");
            if (an.DefaultXbe is { } x)
            {
                Console.WriteLine($"Title:        {x.TitleName}  [{x.TitleIdHex} / {x.TitleIdDisplay}]  {x.RegionLabel}  v{x.Version}  disc {x.DiscNumber}");
            }
            Console.WriteLine($"Image size:   {Bytes(an.ImageLength)} (logical)");
            Console.WriteLine($"Video part.:  {Bytes(an.VideoPartitionBytes)}");
            Console.WriteLine($"Used:         {Bytes(an.UsedBytes)}  ({an.FileCount} files, {an.DirectoryCount} dirs, {Bytes(an.FileBytes)} of file data)");
            Console.WriteLine($"Unused tail:  {Bytes(an.TrailingUnusedBytes)}   Interior gaps: {Bytes(an.InteriorGapBytes)}");
            if (an.Duplicates.Count > 0)
            {
                Console.WriteLine($"Duplicates:   {an.Duplicates.Count} groups, {Bytes(an.DuplicateBytes)} reclaimable");
            }
            foreach (var w in an.Warnings)
            {
                Console.WriteLine($"Warning:      {w}");
            }
        }

        private static int List(string path)
        {
            using var volume = XdvdfsVolume.Open(path);
            foreach (var e in volume.EnumerateEntries())
            {
                Console.WriteLine(e.IsDirectory
                    ? $"{"<dir>",14}  {e.Path}/"
                    : $"{e.Size,14:N0}  {e.Path}");
            }
            Console.WriteLine($"\n{volume.FileCount} files, {volume.DirectoryCount} directories, {Bytes(volume.TotalFileBytes)}");
            return 0;
        }

        private static ShrinkRuleSet? LoadRules(Args a)
        {
            var files = a.Values("--rules");
            return files.Count == 0 ? null : ShrinkRuleSet.Merge(files.Select(ShrinkRuleSet.Load).ToArray());
        }

        private static int Analyze(Args a)
        {
            using var shrinker = XisoShrinker.Open(a.Positional(0));
            var an = shrinker.Analyze(new AnalyzeOptions { FindDuplicates = a.Has("--dupes") }, ConsoleProgress.Create());
            ConsoleProgress.Clear();
            PrintSummary(an);
            Console.WriteLine();
            foreach (var f in an.Findings)
            {
                Console.WriteLine($"[{f.Confidence,-6}] {f.Kind,-19} {Bytes(f.Bytes),10}  {f.Path}");
                Console.WriteLine($"         {f.Message}");
            }
            var plan = shrinker.CreatePlan(an, new ShrinkOptions(), LoadRules(a));
            PrintPlan(plan);
            return 0;
        }

        private static void PrintPlan(ShrinkPlan plan)
        {
            Console.WriteLine("\nPlan:");
            if (plan.Items.Count == 0)
            {
                Console.WriteLine("  (no removals proposed)");
            }
            foreach (var i in plan.Items)
            {
                Console.WriteLine($"  {(i.Remove ? "REMOVE" : "keep  ")} {Bytes(i.Bytes),10}  {i.Path}{(i.IsDirectory ? "/" : "")}  ({i.Source}, {i.Confidence})");
            }
            foreach (var w in plan.Warnings)
            {
                Console.WriteLine($"  warning: {w}");
            }
            Console.WriteLine($"  Removed: {plan.RemovedFileCount} files, {Bytes(plan.RemovedBytes)}.  Estimated output: {Bytes(plan.EstimatedOutputBytes)} (before compression)");
        }

        private static async Task<int> Shrink(Args a, CancellationToken ct)
        {
            string input = a.Positional(0);
            string output = a.Positional(1);
            var options = new ShrinkOptions
            {
                Mode = a.Value("--mode") switch
                {
                    null or "rebuild" => ShrinkMode.Rebuild,
                    "trim" => ShrinkMode.TrimOnly,
                    var m => throw new ArgumentException($"Unknown mode '{m}'."),
                },
                Format = ParseFormat(a.Value("--format")),
                SplitSize = SplitSize(a),
                Deduplicate = a.Has("--dedupe"),
                ExcludePatterns = a.Values("--exclude"),
                ApplyUnverifiedRules = a.Has("--apply-unverified"),
                RemoveLikelyDemos = a.Has("--remove-likely-demos"),
                RemoveSystemUpdate = !a.Has("--keep-systemupdate"),
                MediaPatch = !a.Has("--no-media-patch"),
                Scrub = !a.Has("--no-scrub"),
                Timestamp = a.Has("--deterministic") ? TimestampMode.Deterministic : TimestampMode.PreserveSource,
                Verify = !a.Has("--no-verify"),
                WriteSidecar = !a.Has("--no-sidecar"),
                HashSource = a.Has("--hash"),
                Dat = a.Value("--dat") is { } dat ? DatFile.Load(dat) : null,
                Overwrite = a.Has("--overwrite"),
            };

            using var shrinker = XisoShrinker.Open(input);
            var analysis = await shrinker.AnalyzeAsync(new AnalyzeOptions { FindDuplicates = options.Deduplicate }, ConsoleProgress.Create(), ct);
            ConsoleProgress.Clear();
            var plan = shrinker.CreatePlan(analysis, options, LoadRules(a));
            PrintSummary(analysis);
            PrintPlan(plan);
            if (a.Has("--dry-run"))
            {
                return 0;
            }

            Console.WriteLine();
            var result = await shrinker.ExecuteAsync(plan, output, ConsoleProgress.Create(), ct);
            ConsoleProgress.Clear();
            Console.WriteLine($"Wrote {string.Join(", ", result.OutputFiles.Select(f => Path.GetFileName(f)))}");
            Console.WriteLine($"  {Bytes(result.SourceBytes)} -> {Bytes(result.OutputBytes)}  (saved {Bytes(result.SavedBytes)}, {result.SavedFraction:P1}) in {result.Elapsed:mm\\:ss}");
            Console.WriteLine($"  removed {result.RemovedFileCount} files ({Bytes(result.RemovedBytes)}), deduplicated {result.DeduplicatedFiles} ({Bytes(result.DeduplicatedBytes)}), patched {result.PatchedExecutables} xbe, scrubbed {Bytes(result.ScrubbedBytes)}");
            if (result.Verification is { } v)
            {
                Console.WriteLine($"  verify: {(v.Success ? "OK" : "FAILED")}  sha1 {v.LogicalSha1}");
            }
            if (result.DatMatch is { } datMatch)
            {
                Console.WriteLine($"  source matched DAT: {datMatch.GameName}");
            }
            if (result.SidecarPath is not null)
            {
                Console.WriteLine($"  sidecar: {Path.GetFileName(result.SidecarPath)}");
            }
            return 0;
        }

        private static int Verify(string path, CancellationToken ct)
        {
            using (var source = ImageOpener.Open(path))
            {
                Console.WriteLine($"{source.Format}, {Bytes(source.Length)} logical");
            }
            // Hash the whole logical image (decompressing CCI/CSO) and walk the filesystem.
            var r = XisoShrinker.VerifyImage(path, progress: ConsoleProgress.Create(), cancellationToken: ct, hashImage: true);
            ConsoleProgress.Clear();
            var problems = r.Problems.ToList();
            Console.WriteLine($"Files: {r.FileCount}   default.xbe: {r.DefaultXbe?.TitleName ?? "(none)"}   logical sha1: {r.LogicalSha1}");
            foreach (var p in problems)
            {
                Console.WriteLine($"Problem: {p}");
            }
            Console.WriteLine(problems.Count == 0 ? "OK" : "FAILED");
            return problems.Count == 0 ? 0 : 3;
        }

        private static int ConvertCmd(Args a, CancellationToken ct)
        {
            var r = XisoShrinker.ConvertImage(a.Positional(0), a.Positional(1), ParseFormat(a.Value("--format")), SplitSize(a), a.Has("--overwrite"), ConsoleProgress.Create(), ct);
            ConsoleProgress.Clear();
            Console.WriteLine($"Wrote {string.Join(", ", r.Files.Select(f => Path.GetFileName(f)))} ({Bytes(r.BytesOnDisk)})");
            return 0;
        }

        private static int Pack(Args a, CancellationToken ct)
        {
            var r = XisoShrinker.PackFolder(a.Positional(0), a.Positional(1), ParseFormat(a.Value("--format")),
                new XisoBuildOptions { MediaPatch = !a.Has("--no-media-patch"), FileTime = (ulong)DateTime.UtcNow.ToFileTimeUtc() },
                SplitSize(a), a.Has("--overwrite"), ConsoleProgress.Create(), ct);
            ConsoleProgress.Clear();
            Console.WriteLine($"Wrote {string.Join(", ", r.Files.Select(f => Path.GetFileName(f)))} ({Bytes(r.BytesOnDisk)})");
            return 0;
        }

        private static int Extract(Args a, CancellationToken ct)
        {
            using var volume = XdvdfsVolume.Open(a.Positional(0));
            volume.ExtractTo(a.Positional(1), ConsoleProgress.Create(), ct);
            ConsoleProgress.Clear();
            Console.WriteLine($"Extracted {volume.FileCount} files.");
            return 0;
        }

        private static int Hash(Args a, CancellationToken ct)
        {
            var files = SplitParts.Resolve(a.Positional(0));
            var h = FileHashes.Compute(files, ConsoleProgress.Create(), ct);
            ConsoleProgress.Clear();
            Console.WriteLine($"size  {h.Size}\ncrc32 {h.Crc32}\nmd5   {h.Md5}\nsha1  {h.Sha1}");
            if (a.Value("--dat") is { } dat)
            {
                var match = DatFile.Load(dat).Match(h);
                Console.WriteLine(match is null ? "No DAT match." : $"DAT match: {match.GameName} ({match.RomName})");
            }
            return 0;
        }

        private static OutputFormat ParseFormat(string? s) => s?.ToLowerInvariant() switch
        {
            null or "iso" => OutputFormat.Iso,
            "cci" => OutputFormat.Cci,
            "cso" => OutputFormat.Cso,
            _ => throw new ArgumentException($"Unknown format '{s}'."),
        };

        private static long? SplitSize(Args a)
        {
            if (a.Value("--split-size") is { } s)
            {
                return long.Parse(s);
            }
            return a.Has("--split") ? XdvdfsConstants.FatxSplitSize : null;
        }

        internal static string Bytes(long b)
        {
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
            double v = b;
            int u = 0;
            while (Math.Abs(v) >= 1024 && u < units.Length - 1)
            {
                v /= 1024;
                u++;
            }
            return u == 0 ? $"{b} B" : $"{v:0.00} {units[u]}";
        }
    }

    internal sealed class Args
    {
        private static readonly HashSet<string> Flags = new()
        {
            "--dupes", "--split", "--dedupe", "--apply-unverified", "--remove-likely-demos", "--keep-systemupdate",
            "--no-media-patch", "--no-scrub", "--deterministic", "--no-verify", "--no-sidecar", "--hash",
            "--overwrite", "--dry-run", "--keep",
        };

        private readonly List<string> _positional = new();
        private readonly HashSet<string> _flags = new();
        private readonly Dictionary<string, List<string>> _values = new();

        public Args(IEnumerable<string> args)
        {
            using var e = args.GetEnumerator();
            while (e.MoveNext())
            {
                string arg = e.Current;
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    _positional.Add(arg);
                }
                else if (Flags.Contains(arg))
                {
                    _flags.Add(arg);
                }
                else if (e.MoveNext())
                {
                    if (!_values.TryGetValue(arg, out var list))
                    {
                        _values[arg] = list = new List<string>();
                    }
                    list.Add(e.Current);
                }
                else
                {
                    throw new ArgumentException($"Option {arg} needs a value.");
                }
            }
        }

        public string Positional(int i) =>
            i < _positional.Count ? _positional[i] : throw new ArgumentException($"Missing argument #{i + 1}. Run 'xiso-kit help'.");

        public bool Has(string flag) => _flags.Contains(flag);

        public string? Value(string name) => _values.TryGetValue(name, out var l) ? l[^1] : null;

        public IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out var l) ? l : Array.Empty<string>();
    }

    internal static class ConsoleProgress
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static IProgress<OperationProgress> Create() => new Reporter();

        internal static int Width()
        {
            try
            {
                return Math.Max(20, Console.WindowWidth - 1);
            }
            catch (IOException)
            {
                return 79;
            }
        }

        public static void Clear()
        {
            if (!Console.IsOutputRedirected)
            {
                Console.Write("\r" + new string(' ', Width()) + "\r");
            }
        }

        private sealed class Reporter : IProgress<OperationProgress>
        {
            private long _last;

            public void Report(OperationProgress p)
            {
                if (Console.IsOutputRedirected)
                {
                    return;
                }
                long now = Clock.ElapsedMilliseconds;
                if (now - _last < 200 && p.Stage != OperationStage.Finished)
                {
                    return;
                }
                _last = now;
                string pct = p.Fraction is { } f ? $"{f:P0}" : "";
                string line = $"{p.Stage} {pct} {CliApp.Bytes(p.Done)}/{CliApp.Bytes(p.Total)}";
                int width = Width();
                Console.Write("\r" + (line.Length > width ? line[..width] : line.PadRight(width)));
            }
        }
    }
}
