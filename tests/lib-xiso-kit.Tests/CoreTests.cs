using System.Buffers.Binary;
using System.Text;
using LibXisoKit.Analysis;
using LibXisoKit.Compression;
using LibXisoKit.Identity;
using LibXisoKit.IO;
using LibXisoKit.Shrinking;
using LibXisoKit.Xbe;
using LibXisoKit.Xdvdfs;
using Xunit;

namespace LibXisoKit.Tests;

public class Lz4Tests
{
    public static IEnumerable<object[]> Inputs()
    {
        var rng = new Random(7);
        byte[] R(int n) { var b = new byte[n]; rng.NextBytes(b); return b; }
        yield return new object[] { Array.Empty<byte>() };
        yield return new object[] { new byte[] { 1 } };
        yield return new object[] { R(2048) };
        yield return new object[] { Enumerable.Repeat((byte)0xFF, 2048).ToArray() };
        yield return new object[] { Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcd", 512))) };
        yield return new object[] { Enumerable.Range(0, 70_000).Select(i => (byte)(i % 251)).ToArray() };
        for (int i = 0; i < 50; i++)
        {
            int n = rng.Next(0, 5000);
            yield return new object[] { Enumerable.Range(0, n).Select(_ => (byte)"ab\0"[rng.Next(3)]).ToArray() };
        }
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void RoundTrips(byte[] input)
    {
        var compressed = new byte[Lz4Block.MaxCompressedLength(input.Length)];
        int len = Lz4Block.Compress(input, compressed);
        Assert.True(len > 0);
        var output = new byte[input.Length];
        int n = Lz4Block.Decompress(compressed.AsSpan(0, len), output);
        Assert.Equal(input.Length, n);
        Assert.Equal(input, output);
    }

    /// <summary>The compressor must keep emitting the same bytes (CCI/CSO output is promised byte-identical).</summary>
    [Fact]
    public void CompressorOutputMatchesReference()
    {
        var rng = new Random(11);
        var dst = new byte[Lz4Block.MaxCompressedLength(70_000)];
        var expected = new byte[dst.Length];
        for (int i = 0; i < 3000; i++)
        {
            int n = i % 10 == 0 ? rng.Next(0, 70_000) : 2048;
            var src = new byte[n];
            int kind = i % 5;
            for (int k = 0; k < n; k++)
            {
                src[k] = kind switch
                {
                    0 => (byte)rng.Next(),
                    1 => 0,
                    2 => (byte)(k % (1 + i % 13)),
                    3 => (byte)(rng.Next(8) == 0 ? rng.Next() : 0),
                    _ => (byte)"ab\0"[rng.Next(3)],
                };
            }
            // Full-size and too-small destinations; calls interleave so the reused match table is exercised.
            foreach (int cap in new[] { dst.Length, n / 2 + 1 })
            {
                int want = Lz4Reference.Compress(src, expected.AsSpan(0, cap));
                int got = Lz4Block.Compress(src, dst.AsSpan(0, cap));
                Assert.Equal(want, got);
                if (got > 0)
                {
                    Assert.True(expected.AsSpan(0, want).SequenceEqual(dst.AsSpan(0, got)));
                }
            }
        }
    }

    [Fact]
    public void ReportsOverflow()
    {
        var input = new byte[2048];
        new Random(1).NextBytes(input);
        Assert.Equal(-1, Lz4Block.Compress(input, new byte[100]));
    }
}

/// <summary>The LZ4 compressor as first released (zeroed table per call, bytewise match extension).</summary>
internal static class Lz4Reference
{
    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int n = source.Length, pos = 0;
        bool Literals(ReadOnlySpan<byte> lit, Span<byte> dst, ref int p, int? offset, int m)
        {
            int l = lit.Length;
            int need = 1 + (l >= 15 ? (l - 15) / 255 + 1 : 0) + l + (offset is null ? 0 : 2 + (m >= 15 ? (m - 15) / 255 + 1 : 0));
            if (p + need > dst.Length)
            {
                return false;
            }
            int token = p++;
            dst[token] = (byte)(Math.Min(l, 15) << 4);
            if (l >= 15)
            {
                int v = l - 15;
                for (; v >= 255; v -= 255) { dst[p++] = 255; }
                dst[p++] = (byte)v;
            }
            lit.CopyTo(dst[p..]);
            p += l;
            if (offset is { } o)
            {
                dst[p++] = (byte)o;
                dst[p++] = (byte)(o >> 8);
                dst[token] |= (byte)Math.Min(m, 15);
                if (m >= 15)
                {
                    int v = m - 15;
                    for (; v >= 255; v -= 255) { dst[p++] = 255; }
                    dst[p++] = (byte)v;
                }
            }
            return true;
        }

        if (n < 13)
        {
            return Literals(source, destination, ref pos, null, 0) ? pos : -1;
        }
        var table = new int[4096];
        int anchor = 0, ip = 0, limit = n - 12, matchLimit = n - 5;
        while (ip < limit)
        {
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(ip, 4));
            int h = (int)((seq * 2654435761u) >> 20);
            int candidate = table[h] - 1;
            table[h] = ip + 1;
            if (candidate >= 0 && ip - candidate <= 65535 && BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(candidate, 4)) == seq)
            {
                int len = 4;
                while (ip + len < matchLimit && source[candidate + len] == source[ip + len]) { len++; }
                while (ip > anchor && candidate > 0 && source[ip - 1] == source[candidate - 1]) { ip--; candidate--; len++; }
                if (!Literals(source.Slice(anchor, ip - anchor), destination, ref pos, ip - candidate, len - 4))
                {
                    return -1;
                }
                ip += len;
                anchor = ip;
            }
            else
            {
                ip++;
            }
        }
        return Literals(source[anchor..], destination, ref pos, null, 0) ? pos : -1;
    }
}

public class GlobTests
{
    [Theory]
    [InlineData("**/$SystemUpdate/**", "/$SystemUpdate", true)]
    [InlineData("**/$SystemUpdate/**", "/$systemupdate/xboxupd.xbe", true)]
    [InlineData("**/$SystemUpdate/**", "/Media/$SystemUpdate/a", true)]
    [InlineData("/Demos/**", "/Demos", true)]
    [InlineData("/Demos/**", "/demos/a/b.xbe", true)]
    [InlineData("/Demos/**", "/Data/Demos/a", false)]
    [InlineData("Demos/**", "/Data/Demos/a", true)]
    [InlineData("*.xmv", "/Media/intro.xmv", true)]
    [InlineData("/*.xmv", "/Media/intro.xmv", false)]
    [InlineData("/Media/intro?.xmv", "/Media/intro1.xmv", true)]
    [InlineData("/Media/[ab]*.bin", "/Media/b2.bin", true)]
    [InlineData("/Media/[!ab]*.bin", "/Media/b2.bin", false)]
    public void Matches(string pattern, string path, bool expected) =>
        Assert.Equal(expected, new GlobMatcher(new[] { pattern }).IsMatch(path));
}

public class FormatTests
{
    [Fact]
    public void NameComparerMatchesExtractXiso()
    {
        var c = XdvdfsNameComparer.Instance;
        Assert.Equal(0, c.Compare("default.xbe", "DEFAULT.XBE"));
        Assert.True(c.Compare("Ab_", "aB") > 0);   // longer after shorter prefix
        Assert.True(c.Compare("_", "a") > 0);      // 'a' folds to 'A' (0x41), which sorts before '_' (0x5F)
    }

    [Fact]
    public void Crc32KnownValue() => Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));

    [Fact]
    public void ParsesXbe()
    {
        var info = XbeReader.TryParse(TestImages.FakeXbe(0x4D530004, "Halo"));
        Assert.NotNull(info);
        Assert.Equal("4D530004", info!.TitleIdHex);
        Assert.Equal("MS-004", info.TitleIdDisplay);
        Assert.Equal("Halo", info.TitleName);
        Assert.Equal("GLO", info.RegionLabel);
    }

    [Fact]
    public void MediaPatchIsIdempotent()
    {
        var x = TestImages.FakeXbe(1, "x");
        Assert.Equal(1, XbeMediaPatch.Apply(x));
        Assert.Equal(0, XbeMediaPatch.Apply(x));
    }

    [Fact]
    public void RulesJsonRoundTrips()
    {
        var set = ShrinkRuleSet.BuiltIn;
        set.Titles.Add(new TitleRules { TitleId = "4D530004", Versions = { "0x2" }, Rules = { new ShrinkRule { Remove = { "/Demos/**" } } } });
        var back = ShrinkRuleSet.Parse(set.ToJson());
        Assert.Single(back.Titles);
        Assert.True(back.Titles[0].Matches(0x4D530004, 2));
        Assert.False(back.Titles[0].Matches(0x4D530004, 3));
    }
}

public sealed class ImageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xiso-kit-tests-" + Guid.NewGuid().ToString("N"));

    public ImageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(OutputFormat.Iso)]
    [InlineData(OutputFormat.Cci)]
    [InlineData(OutputFormat.Cso)]
    public void PackReadBack(OutputFormat format)
    {
        string tree = TestImages.CreateTree(Path.Combine(_dir, "tree"));
        string output = Path.Combine(_dir, "out" + ImageWriter.ExtensionFor(format));
        XisoShrinker.PackFolder(tree, output, format, new XisoBuildOptions { MediaPatch = false });

        using var volume = XdvdfsVolume.Open(output);
        foreach (var file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
        {
            string rel = "/" + Path.GetRelativePath(tree, file).Replace('\\', '/');
            var entry = volume.Find(rel);
            Assert.NotNull(entry);
            Assert.Equal(File.ReadAllBytes(file), volume.ReadAllBytes(entry!));
        }
        Assert.True(volume.IsOptimized);
        Assert.Empty(volume.Warnings);
    }

    [Fact]
    public void ShrinkRemovesSystemUpdateAndDedupes()
    {
        string tree = TestImages.CreateTree(Path.Combine(_dir, "tree"));
        string iso = Path.Combine(_dir, "in.iso");
        XisoShrinker.PackFolder(tree, iso, OutputFormat.Iso, new XisoBuildOptions { MediaPatch = false });

        using var shrinker = XisoShrinker.Open(iso);
        var analysis = shrinker.Analyze(new AnalyzeOptions { FindDuplicates = true });
        var plan = shrinker.CreatePlan(analysis, new ShrinkOptions { Deduplicate = true });
        var result = shrinker.Execute(plan, Path.Combine(_dir, "out.iso"));

        Assert.True(result.Verification!.Success);
        Assert.Equal(1, result.DeduplicatedFiles);
        using var output = XdvdfsVolume.Open(result.OutputFiles[0]);
        Assert.Null(output.Find("/$SystemUpdate"));
        Assert.NotNull(output.Find("/default.xbe"));
        var a = output.Find("/Data/a.bin")!;
        var b = output.Find("/Data/b.bin")!;
        Assert.Equal(a.StartSector, b.StartSector);
    }

    [Fact]
    public void SplitIsoReadsAsOne()
    {
        string tree = TestImages.CreateTree(Path.Combine(_dir, "tree"));
        string output = Path.Combine(_dir, "split.iso");
        var r = XisoShrinker.PackFolder(tree, output, OutputFormat.Iso, new XisoBuildOptions(), splitSize: 32 * 2048);
        Assert.True(r.Files.Count > 1);
        using var source = ImageOpener.Open(r.Files[^1]);
        Assert.Equal(r.LogicalLength, source.Length);
    }
}

public sealed class FastPathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xiso-kit-tests-" + Guid.NewGuid().ToString("N"));

    public FastPathTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A split XISO with random data appended to its last part, so the tail past the used area counts.</summary>
    private string[] SplitSourceWithTail()
    {
        string tree = TestImages.CreateTree(Path.Combine(_dir, "tree"));
        var packed = XisoShrinker.PackFolder(tree, Path.Combine(_dir, "src.iso"), OutputFormat.Iso, new XisoBuildOptions { MediaPatch = false }, splitSize: 64 * 2048);
        var tail = new byte[3 * 2048 + 100];
        new Random(5).NextBytes(tail);
        using (var fs = new FileStream(packed.Files[^1], FileMode.Append))
        {
            fs.Write(tail);
        }
        return packed.Files.ToArray();
    }

    [Theory]
    [InlineData(OutputFormat.Iso, true)]
    [InlineData(OutputFormat.Cci, false)]
    public void TrimHashesSourceWhileWriting(OutputFormat format, bool mediaPatch)
    {
        var parts = SplitSourceWithTail();
        var expected = FileHashes.Compute(parts);

        using var shrinker = XisoShrinker.Open(parts[0]);
        var plan = shrinker.CreatePlan(shrinker.Analyze(), new ShrinkOptions
        {
            Mode = ShrinkMode.TrimOnly, RemoveSystemUpdate = false, MediaPatch = mediaPatch, Format = format, HashSource = true,
        });
        var result = shrinker.Execute(plan, Path.Combine(_dir, "out" + ImageWriter.ExtensionFor(format)));

        Assert.Equal(expected, result.SourceHashes);
        Assert.True(result.Verification!.Success);
        Assert.Equal(mediaPatch, result.PatchedExecutables > 0);
    }

    [Theory]
    [InlineData("", 0xEF46DB3751D8E999UL)]
    [InlineData("abc", 0x44BC2CF5AD770999UL)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0x0B242D361FDA71BCUL)]
    public void XxHash64KnownValues(string text, ulong expected) =>
        Assert.Equal(expected, XxHash64.Compute(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void XxHash64StreamsLikeOneShot()
    {
        var rng = new Random(2);
        var data = new byte[100_003];
        rng.NextBytes(data);
        var x = new XxHash64();
        for (int pos = 0; pos < data.Length;)
        {
            int n = Math.Min(data.Length - pos, rng.Next(1, 70));
            x.Append(data.AsSpan(pos, n));
            pos += n;
        }
        Assert.Equal(XxHash64.Compute(data), x.Digest());
        Assert.Equal(0xF80E7B96315AFFFAUL, XxHash64.Compute(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("0123456789", 10)))));
    }

    [Theory]
    [InlineData(OutputFormat.Iso)]
    [InlineData(OutputFormat.Cci)]
    public void FastVerifyChecksTheWrittenImage(OutputFormat format)
    {
        var parts = SplitSourceWithTail();
        using var shrinker = XisoShrinker.Open(parts[0]);
        var plan = shrinker.CreatePlan(shrinker.Analyze(), new ShrinkOptions { Mode = ShrinkMode.TrimOnly, RemoveSystemUpdate = false, Format = format, FastVerify = true });
        var result = shrinker.Execute(plan, Path.Combine(_dir, "fast" + ImageWriter.ExtensionFor(format)));
        Assert.True(result.Verification!.Success);
        Assert.Null(result.Verification.LogicalSha1);
        Assert.Equal(XisoShrinker.VerifyImage(result.OutputFiles[0], hashImage: true).LogicalSha1, result.LogicalSha1);

        // A flipped byte in file data is caught.
        using var source = ImageOpener.Open(result.OutputFiles[0]);
        var written = ImageWriter.Write(source, Path.Combine(_dir, "flip.iso"), OutputFormat.Iso, null, false, fastChecksum: true, null, CancellationToken.None);
        using (var fs = new FileStream(written.Files[0], FileMode.Open))
        {
            fs.Position = fs.Length - 4096;
            int b = fs.ReadByte();
            fs.Position--;
            fs.WriteByte((byte)~b);
        }
        Assert.True(XisoShrinker.VerifyImage(written.Files[0], null, written.LogicalXxHash64, null, null, CancellationToken.None, false).Success == false);
    }

    [Fact]
    public void TeeHashesEveryByteOnceWhateverTheReadOrder()
    {
        var parts = SplitSourceWithTail();
        var expected = FileHashes.Compute(parts);
        using var inner = new ConcatenatedFileSource(parts);
        using var tee = new HashingTeeSource(inner, CancellationToken.None);
        var rng = new Random(3);
        long[] starts = { 4096, 0, 70_000, 69_000, inner.Length - 5000, 10, inner.Length + 10 };
        foreach (long start in starts)
        {
            var got = new byte[rng.Next(1, 300_000)];
            var want = new byte[got.Length];
            Assert.Equal(inner.Read(start, want), tee.Read(start, got));
            Assert.Equal(want, got);
        }
        Assert.Equal(expected, tee.Finish(null));
    }

    [Fact]
    public void TeeStopsOnCancellationAndDisposes()
    {
        var parts = SplitSourceWithTail();
        using var inner = new ConcatenatedFileSource(parts);
        using var cts = new CancellationTokenSource();
        var tee = new HashingTeeSource(inner, cts.Token);
        tee.Read(0, new byte[4096]);
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => tee.Read(inner.Length - 2048, new byte[2048]));
        Assert.ThrowsAny<OperationCanceledException>(() => tee.Finish(null));
        Assert.True(Task.Run(tee.Dispose).Wait(TimeSpan.FromSeconds(10)), "Dispose hung after cancellation.");
    }
}

internal static class TestImages
{
    public static string CreateTree(string root)
    {
        var rng = new Random(42);
        void Write(string rel, byte[] data)
        {
            string p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
        }
        byte[] Bytes(int n) { var b = new byte[n]; rng.NextBytes(b); return b; }

        Write("default.xbe", FakeXbe(0x4E490001, "Test"));
        var dup = Bytes(10_000);
        Write("Data/a.bin", dup);
        Write("Data/b.bin", dup);
        Write("Data/c.bin", Bytes(70_000));
        Write("Data/empty.bin", Array.Empty<byte>());
        Write("$SystemUpdate/xboxupd.xbe", Bytes(5_000));
        for (int i = 0; i < 120; i++)
        {
            Write($"Data/many/{i:D3}{new string('n', i % 60)}.bin", Bytes(rng.Next(0, 3000)));
        }
        return root;
    }

    public static byte[] FakeXbe(uint titleId, string name)
    {
        var x = new byte[16 * 1024];
        "XBEH"u8.CopyTo(x);
        BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(0x104), 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(0x118), 0x10000 + 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(0x1000 + 8), titleId);
        Encoding.Unicode.GetBytes(name).CopyTo(x.AsSpan(0x1000 + 0x0C));
        BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(0x1000 + 0xA0), 7);
        XbeMediaPatch.Pattern.CopyTo(x.AsSpan(0x3000));
        return x;
    }
}
