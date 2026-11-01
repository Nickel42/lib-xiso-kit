using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LibXisoKit.Analysis;
using LibXisoKit.IO;
using LibXisoKit.Shrinking;
using LibXisoKit.Xbe;
using LibXisoKit.Xdvdfs;

namespace LibXisoKit.Cli;

/// <summary>
/// End-to-end check that needs no game: builds a synthetic disc (with a fake Redump video partition,
/// demos, a dashboard update and duplicate files), then exercises every shrink path and checks the results.
/// </summary>
internal static class SelfTest
{
    private static int _failures;

    public static int Run(bool keep)
    {
        string work = Path.Combine(Path.GetTempPath(), "xiso-kit-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        Console.WriteLine($"Working in {work}");
        try
        {
            string tree = Path.Combine(work, "tree");
            CreateTree(tree);

            // 1. Pack the folder (no media patch so source bytes stay comparable).
            string xiso = Path.Combine(work, "packed.iso");
            var packed = XisoShrinker.PackFolder(tree, xiso, OutputFormat.Iso, new XisoBuildOptions { MediaPatch = false });
            Check("pack folder", File.Exists(xiso) && packed.LogicalLength % 0x10000 == 0);
            CompareWithTree(xiso, tree, mediaPatched: false, excluded: _ => false, label: "packed contents");

            // 2. Fake Redump: 387 MiB video partition + game partition + random tail.
            string redump = Path.Combine(work, "redump.iso");
            using (var fs = new FileStream(redump, FileMode.CreateNew))
            {
                var noise = RandomNumberGenerator.GetBytes(1 << 20);
                fs.Write(noise);
                fs.SetLength(XdvdfsConstants.Xgd1PartitionOffset);
                fs.Position = XdvdfsConstants.Xgd1PartitionOffset;
                using (var p = File.OpenRead(xiso))
                {
                    p.CopyTo(fs);
                }
                fs.Write(RandomNumberGenerator.GetBytes(3 << 20));
            }

            using (var shrinker = XisoShrinker.Open(redump))
            {
                Check("detect redump layout", shrinker.Volume.Layout == DiscLayout.RedumpXgd1);
                var analysis = shrinker.Analyze(new AnalyzeOptions { FindDuplicates = true });
                Check("parse default.xbe", analysis.DefaultXbe?.TitleId == 0x4E490001 && analysis.DefaultXbe.TitleName == "Self Test");
                Check("find $SystemUpdate", analysis.Findings.Any(f => f.Kind == FindingKind.SystemUpdate));
                Check("find demo folder", analysis.Findings.Any(f => f.Kind == FindingKind.DemoContent && f.Confidence == Confidence.Likely));
                Check("find duplicates", analysis.Duplicates.Count == 1 && analysis.Duplicates[0].Paths.Count == 3);

                var rules = ShrinkRuleSet.Parse("""
                    {
                      // comments and trailing commas are allowed
                      "version": 1,
                      "titles": [
                        { "titleId": "4E490001", "name": "Self Test",
                          "rules": [ { "remove": [ "/Extras/Trailer.xmv" ], "reason": "test rule", "verified": true } ] },
                      ],
                    }
                    """);

                var plan = shrinker.CreatePlan(analysis, new ShrinkOptions { Deduplicate = true, RemoveLikelyDemos = true, Overwrite = true }, rules);
                Check("plan: systemupdate selected", plan.Items.Any(i => i.Path == "/$SystemUpdate" && i.Remove && i.Source == RemovalSource.BuiltIn));
                Check("plan: demo selected", plan.Items.Any(i => i.Path == "/Demos" && i.Remove));
                Check("plan: rule selected", plan.Items.Any(i => i.Path == "/Extras/Trailer.xmv" && i.Remove && i.Source == RemovalSource.VerifiedRule));
                Func<string, bool> removed = p =>
                    p.StartsWith("/$SystemUpdate", StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith("/Demos", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("/Extras/Trailer.xmv", StringComparison.OrdinalIgnoreCase);

                foreach (var format in new[] { OutputFormat.Iso, OutputFormat.Cci, OutputFormat.Cso })
                {
                    string output = Path.Combine(work, "rebuilt" + ImageWriter.ExtensionFor(format));
                    plan.Options = plan.Options with { Format = format };
                    var r = shrinker.Execute(plan, output);
                    Check($"rebuild {format}: verified", r.Verification?.Success == true);
                    Check($"rebuild {format}: dedupe", r.DeduplicatedFiles == 2);
                    Check($"rebuild {format}: media patch", r.PatchedExecutables >= 1);
                    Check($"rebuild {format}: smaller", r.OutputBytes < new FileInfo(xiso).Length);
                    Check($"rebuild {format}: sidecar", r.SidecarPath is not null && File.Exists(r.SidecarPath));
                    CompareWithTree(output, tree, mediaPatched: true, removed, $"rebuild {format} contents");
                }

                // Deterministic output: same input + options => same bytes.
                plan.Options = plan.Options with { Format = OutputFormat.Iso, Timestamp = TimestampMode.Deterministic, WriteSidecar = false };
                var d1 = shrinker.Execute(plan, Path.Combine(work, "det1.iso"));
                var d2 = shrinker.Execute(plan, Path.Combine(work, "det2.iso"));
                Check("deterministic output", d1.LogicalSha1 == d2.LogicalSha1);

                // Split output.
                plan.Options = plan.Options with { SplitSize = 64 * 2048 };
                var split = shrinker.Execute(plan, Path.Combine(work, "split.iso"));
                Check("split into parts", split.OutputFiles.Count > 1 && split.OutputFiles[0].EndsWith(".1.iso", StringComparison.Ordinal));
                CompareWithTree(split.OutputFiles[1], tree, mediaPatched: true, removed, "split contents (opened via part 2)");

                plan.Options = plan.Options with { Format = OutputFormat.Cci };
                var splitCci = shrinker.Execute(plan, Path.Combine(work, "split.cci"));
                Check("split CCI", splitCci.OutputFiles.Count > 1 && splitCci.Verification?.Success == true);

                plan.Options = plan.Options with { Format = OutputFormat.Cso };
                var splitCso = shrinker.Execute(plan, Path.Combine(work, "split.cso"));
                Check("split CSO", splitCso.OutputFiles.Count > 1 && splitCso.Verification?.Success == true);
            }

            // 3. Trim-only (layout preserved, filler scrubbed).
            using (var shrinker = XisoShrinker.Open(redump))
            {
                var analysis = shrinker.Analyze();
                var plan = shrinker.CreatePlan(analysis, new ShrinkOptions { Mode = ShrinkMode.TrimOnly, RemoveSystemUpdate = false, MediaPatch = false });
                var r = shrinker.Execute(plan, Path.Combine(work, "trimmed.iso"));
                Check("trim: verified", r.Verification?.Success == true);
                Check("trim: same size as packed", r.OutputBytes == new FileInfo(xiso).Length);
                CompareWithTree(Path.Combine(work, "trimmed.iso"), tree, mediaPatched: false, _ => false, "trim contents");

                var bad = shrinker.CreatePlan(analysis, new ShrinkOptions { Mode = ShrinkMode.TrimOnly });
                bool threw = false;
                try
                {
                    shrinker.Execute(bad, Path.Combine(work, "never.iso"));
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }
                Check("trim refuses removals", threw && !File.Exists(Path.Combine(work, "never.iso")));
            }

            // 4. Container conversion round trip.
            string cci = Path.Combine(work, "converted.cci");
            XisoShrinker.ConvertImage(xiso, cci, OutputFormat.Cci);
            string back = Path.Combine(work, "converted-back.iso");
            XisoShrinker.ConvertImage(cci, back, OutputFormat.Iso);
            Check("ISO -> CCI -> ISO is lossless", FileHash(xiso) == FileHash(back));

            // 5. Existing output is not overwritten by default.
            bool refused = false;
            try
            {
                XisoShrinker.ConvertImage(xiso, back, OutputFormat.Iso);
            }
            catch (IOException)
            {
                refused = true;
            }
            Check("refuses to overwrite", refused);
        }
        finally
        {
            if (!keep)
            {
                try
                {
                    Directory.Delete(work, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        Console.WriteLine(_failures == 0 ? "\nSELFTEST PASSED" : $"\nSELFTEST FAILED ({_failures} checks)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        if (!ok)
        {
            _failures++;
        }
    }

    private static string FileHash(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static void CompareWithTree(string image, string tree, bool mediaPatched, Func<string, bool> excluded, string label)
    {
        using var volume = XdvdfsVolume.Open(image);
        int compared = 0;
        bool ok = true;
        foreach (var file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
        {
            string rel = "/" + Path.GetRelativePath(tree, file).Replace('\\', '/');
            var entry = volume.Find(rel);
            if (excluded(rel))
            {
                if (entry is not null)
                {
                    Console.WriteLine($"        {rel} should have been removed");
                    ok = false;
                }
                continue;
            }
            if (entry is null)
            {
                Console.WriteLine($"        {rel} is missing");
                ok = false;
                continue;
            }
            byte[] expected = File.ReadAllBytes(file);
            if (mediaPatched && XbeMediaPatch.AppliesTo(rel))
            {
                XbeMediaPatch.Apply(expected);
            }
            if (!volume.ReadAllBytes(entry).AsSpan().SequenceEqual(expected))
            {
                Console.WriteLine($"        {rel} differs");
                ok = false;
            }
            compared++;
        }
        bool emptyDir = volume.Find("/EmptyDir") is { IsDirectory: true, Children.Count: 0 };
        Check($"{label} ({compared} files)", ok && emptyDir);
    }

    private static void CreateTree(string root)
    {
        var rng = new Random(1234);
        void Write(string rel, byte[] data)
        {
            string p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
        }
        byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            rng.NextBytes(b);
            return b;
        }

        Write("default.xbe", FakeXbe(0x4E490001, "Self Test"));
        Write("Data/level1.bin", RandomBytes(300_000));
        var dup = RandomBytes(123_457);
        Write("Data/shared_a.bin", dup);
        Write("Data/sub/shared_b.bin", dup);
        Write("Data/shared_c.bin", dup);
        Write("Data/empty.bin", Array.Empty<byte>());
        Write("Media/intro.xmv", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("VIDEO-", 300_000))));
        Write("Extras/Trailer.xmv", RandomBytes(200_000));
        Write("Demos/OtherGame/default.xbe", FakeXbe(0x4E490002, "Demo"));
        Write("Demos/OtherGame/data.bin", RandomBytes(500_000));
        Write("$SystemUpdate/xboxupd.xbe", RandomBytes(150_000));
        for (int i = 0; i < 150; i++)
        {
            Write($"Data/many/f{i:D3}_{new string('x', i % 50)}.dat", RandomBytes(rng.Next(0, 5000)));
        }
        Write("Data/aB.txt", RandomBytes(10));
        Write("Data/Ab_.txt", RandomBytes(11));
        Write("Data/zz~.txt", RandomBytes(12));
        Directory.CreateDirectory(Path.Combine(root, "EmptyDir"));
    }

    internal static byte[] FakeXbe(uint titleId, string name)
    {
        var x = new byte[64 * 1024];
        "XBEH"u8.CopyTo(x);
        BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(0x104), 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(0x118), 0x10000 + 0x1000);
        const int c = 0x1000;
        BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(c), 0x1D0);
        BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(c + 8), titleId);
        Encoding.Unicode.GetBytes(name).CopyTo(x.AsSpan(c + 0x0C));
        BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(c + 0x9C), 0x80000003);
        BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(c + 0xA0), 0x7);
        BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(c + 0xAC), 2);
        XbeMediaPatch.Pattern.CopyTo(x.AsSpan(0x5000));
        return x;
    }
}
