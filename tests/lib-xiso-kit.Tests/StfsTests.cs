using LibXisoKit.Stfs;
using Xunit;
using static LibXisoKit.Tests.StfsBuilder;

namespace LibXisoKit.Tests;

public sealed class StfsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xiso-stfs-" + Guid.NewGuid().ToString("N"));

    public StfsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] Bytes(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    private string Save(byte[] package, string name = "package")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, package);
        return path;
    }

    [Fact]
    public void ReadsTheHeader()
    {
        var path = Save(Build([new("default.xex", -1, Data: Bytes(100, 1))], type: XContentType.ArcadeTitle, titleId: 0x58410A5D, displayName: "Geometry Test"));
        var header = StfsHeader.TryRead(path);
        Assert.NotNull(header);
        Assert.Equal(StfsSignature.Live, header!.Signature);
        Assert.Equal(XContentType.ArcadeTitle, header.ContentType);
        Assert.Equal("Xbox Live Arcade game", header.ContentTypeText);
        Assert.Equal("58410A5D", header.TitleIdText);
        Assert.Equal("Geometry Test", header.DisplayName);
        Assert.Equal("Test Publisher", header.PublisherName);
        Assert.Equal(StfsVolumeKind.Stfs, header.VolumeKind);
        Assert.True(StfsHeader.IsPackage(path));
    }

    [Theory]
    [InlineData("CON ", StfsSignature.Con)]
    [InlineData("PIRS", StfsSignature.Pirs)]
    public void KnowsEverySignature(string magic, StfsSignature expected)
    {
        var path = Save(Build([new("a", -1, Data: [1])], magic: magic));
        Assert.Equal(expected, StfsHeader.TryRead(path)!.Signature);
    }

    [Fact]
    public void RejectsWhatIsntAPackage()
    {
        Assert.Null(StfsHeader.TryRead(Save(Bytes(0x2000, 3), "random")));
        Assert.Null(StfsHeader.TryRead(Save([.. "LIVE"u8], "short")));
        Assert.False(StfsHeader.IsPackage(Path.Combine(_dir, "missing")));
        Assert.Throws<InvalidDataException>(() => StfsPackage.Open(Save(Bytes(0x2000, 3), "random2")).Dispose());
    }

    public static TheoryData<byte> Separations => new() { 1, 0, 2 };

    [Theory]
    [MemberData(nameof(Separations))]
    public void ReadsFilesAndFoldersInEveryLayout(byte blockSeparation)
    {
        // 200 blocks of data: past one level-0 table, so a level-1 table is needed. One file is chained, not contiguous.
        var big = Bytes(150 * 4096 + 77, 5);
        var chained = Bytes(40 * 4096 + 1, 6);
        var small = Bytes(10, 7);
        var path = Save(Build(
        [
            new("Content", -1, Directory: true),
            new("big.bin", 0, Data: big),
            new("chained.bin", 0, Data: chained, Contiguous: false),
            new("default.xex", -1, Data: small),
            new("empty.txt", -1, Data: []),
        ], blockSeparation));

        using var package = StfsPackage.Open(path);
        Assert.Empty(package.Warnings);
        Assert.Equal((blockSeparation & 1) == 0 ? 2 : 1, package.TableCopies);
        Assert.Equal(["Content", "Content/big.bin", "Content/chained.bin", "default.xex", "empty.txt"], package.Entries.Select(e => e.Path));
        Assert.True(package.Find("content\\BIG.bin")!.Size == big.Length);
        Assert.Equal(big, package.ReadAllBytes(package.Find("Content/big.bin")!));
        Assert.Equal(chained, package.ReadAllBytes(package.Find("Content/chained.bin")!));
        Assert.Equal(small, package.ReadAllBytes(package.Find("default.xex")!));
        Assert.Empty(package.ReadAllBytes(package.Find("empty.txt")!));

        var check = package.Verify();
        Assert.True(check.Success, string.Join("; ", check.Problems));
        Assert.True(check.BlocksChecked > 170);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void ReadsAPackageBigEnoughForALevel2Table(byte blockSeparation)
    {
        // Past 28,900 blocks (118 MiB) the hash tree gains a third level, as big XBLA games do.
        var data = new byte[29_100 * 4096];
        foreach (var at in new[] { 0, 170 * 4096, 28_899 * 4096, 28_900 * 4096 + 5, data.Length - 1 })
        {
            data[at] = 0xA5;
        }
        var path = Save(Build([new("big.bin", -1, Data: data)], blockSeparation));
        using var package = StfsPackage.Open(path);
        Assert.Empty(package.Warnings);
        var read = package.ReadAllBytes(package.Entries[0]);
        Assert.True(data.AsSpan().SequenceEqual(read));
        var check = package.Verify();
        Assert.True(check.Success, string.Join("; ", check.Problems.Take(5)));
    }

    [Fact]
    public void ExtractsEverything()
    {
        var data = Bytes(9000, 8);
        var path = Save(Build([new("Media", -1, Directory: true), new("intro.wmv", 0, Data: data)]));
        using var package = StfsPackage.Open(path);
        var output = Path.Combine(_dir, "out");
        package.ExtractTo(output);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(output, "Media", "intro.wmv")));
    }

    [Fact]
    public void VerifyFindsADamagedBlock()
    {
        var package = Build([new("default.xex", -1, Data: Bytes(3 * 4096, 9))]);
        package[SmallDataOffset(2) + 100] ^= 0xFF;
        using var opened = StfsPackage.Open(Save(package));
        var check = opened.Verify();
        Assert.False(check.Success);
        Assert.Contains(check.Problems, p => p.Contains("Block 2"));
    }

    [Fact]
    public void RefusesNamesThatLeaveTheFolder()
    {
        var path = Save(Build([new("..", -1, Data: [1])]));
        Assert.Throws<InvalidDataException>(() => StfsPackage.Open(path).Dispose());
    }

    [Fact]
    public void RefusesAFolderLoop()
    {
        var path = Save(Build([new("a", 1, Directory: true), new("b", 0, Directory: true)]));
        Assert.Throws<InvalidDataException>(() => StfsPackage.Open(path).Dispose());
    }

    [Fact]
    public void OpensGamesOnDemandForItsHeaderOnly()
    {
        using var package = StfsPackage.Open(Save(SvodHeader(0x4D5307E6)));
        Assert.Equal(StfsVolumeKind.Svod, package.Header.VolumeKind);
        Assert.Equal(XContentType.GamesOnDemand, package.Header.ContentType);
        Assert.Equal("4D5307E6", package.Header.TitleIdText);
        Assert.Equal(3u, package.Header.DataFileCount);
        Assert.Equal("A Disc Game", package.Header.DisplayName);
        Assert.Empty(package.Entries);
        Assert.Throws<NotSupportedException>(() => package.Verify());
    }
}
