using System.Buffers.Binary;
using System.Text;
using NoIntrovert.XboxIso.Analysis;
using NoIntrovert.XboxIso.Compression;
using NoIntrovert.XboxIso.Identity;
using NoIntrovert.XboxIso.IO;
using NoIntrovert.XboxIso.Shrinking;
using NoIntrovert.XboxIso.Xbe;
using NoIntrovert.XboxIso.Xdvdfs;
using Xunit;

namespace NoIntrovert.XboxIso.Tests;

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

    [Fact]
    public void ReportsOverflow()
    {
        var input = new byte[2048];
        new Random(1).NextBytes(input);
        Assert.Equal(-1, Lz4Block.Compress(input, new byte[100]));
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
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xboxiso-tests-" + Guid.NewGuid().ToString("N"));

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
        XboxIsoShrinker.PackFolder(tree, output, format, new XisoBuildOptions { MediaPatch = false });

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
        XboxIsoShrinker.PackFolder(tree, iso, OutputFormat.Iso, new XisoBuildOptions { MediaPatch = false });

        using var shrinker = XboxIsoShrinker.Open(iso);
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
        var r = XboxIsoShrinker.PackFolder(tree, output, OutputFormat.Iso, new XisoBuildOptions(), splitSize: 32 * 2048);
        Assert.True(r.Files.Count > 1);
        using var source = ImageOpener.Open(r.Files[^1]);
        Assert.Equal(r.LogicalLength, source.Length);
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
