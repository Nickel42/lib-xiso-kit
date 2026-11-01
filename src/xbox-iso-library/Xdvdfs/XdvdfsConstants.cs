namespace XboxIsoLibrary.Xdvdfs;

/// <summary>On-disc constants of the Xbox DVD filesystem (XDVDFS).</summary>
public static class XdvdfsConstants
{
    public const int SectorSize = 2048;

    /// <summary>Byte offset of the volume descriptor from the start of the game partition (sector 32).</summary>
    public const int DescriptorOffset = 0x10000;

    /// <summary>Sector of the volume descriptor.</summary>
    public const int DescriptorSector = DescriptorOffset / SectorSize;

    /// <summary>Sector where rebuilt images place the root directory table (matches extract-xiso).</summary>
    public const uint RootDirectorySector = 0x108;

    /// <summary>Rebuilt images are padded to a multiple of this size (matches extract-xiso).</summary>
    public const int FileModulus = 0x10000;

    public const int OptimizedTagOffset = 31337;

    public const byte AttributeReadOnly = 0x01;
    public const byte AttributeHidden = 0x02;
    public const byte AttributeSystem = 0x04;
    public const byte AttributeDirectory = 0x10;
    public const byte AttributeArchive = 0x20;
    public const byte AttributeNormal = 0x80;

    public const byte PadByte = 0xFF;

    /// <summary>Size of the fixed part of a directory entry; the name follows.</summary>
    public const int EntryHeaderSize = 14;

    public const int MaxNameLength = 255;

    /// <summary>"MICROSOFT*XBOX*MEDIA"</summary>
    public static ReadOnlySpan<byte> Magic => "MICROSOFT*XBOX*MEDIA"u8;

    /// <summary>Offset of the game partition inside each known whole-disc layout.</summary>
    public const long Xgd1PartitionOffset = 0x18300000;   // original Xbox Redump (387 MiB video partition)
    public const long Xgd2PartitionOffset = 0x0FD90000;   // Xbox 360 XGD2
    public const long Xgd3PartitionOffset = 0x02080000;   // Xbox 360 XGD3
    public const long Xgd2HybridPartitionOffset = 0x89D80000;

    /// <summary>Default size for split ISO parts: sector aligned and below the FATX 4 GiB file limit.</summary>
    public const long FatxSplitSize = 0xFFBF6000;

    /// <summary>Number of sectors needed for <paramref name="bytes"/>.</summary>
    public static long SectorsFor(long bytes) => (bytes + SectorSize - 1) / SectorSize;
}

/// <summary>Physical layout an image was found in.</summary>
public enum DiscLayout
{
    /// <summary>Game partition starts at offset 0 (XISO / extracted game partition).</summary>
    Xiso,
    /// <summary>Full original-Xbox disc dump (Redump) with the 387 MiB video partition.</summary>
    RedumpXgd1,
    /// <summary>Xbox 360 XGD2 disc dump.</summary>
    Xgd2,
    /// <summary>Xbox 360 XGD3 disc dump.</summary>
    Xgd3,
    /// <summary>Xbox 360 XGD2 hybrid disc dump.</summary>
    Xgd2Hybrid,
}

/// <summary>
/// Name ordering used for XDVDFS directory trees: ASCII-only case folding with signed
/// byte comparison, identical to extract-xiso's <c>avl_compare_key</c>.
/// </summary>
public sealed class XdvdfsNameComparer : IComparer<string>
{
    public static readonly XdvdfsNameComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= string.Empty;
        y ??= string.Empty;
        int n = Math.Min(x.Length, y.Length);
        for (int i = 0; i < n; i++)
        {
            int a = Fold(x[i]);
            int b = Fold(y[i]);
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }
        return x.Length.CompareTo(y.Length);
    }

    private static int Fold(char c)
    {
        int v = c & 0xFF; // names are single-byte on disc
        if (v >= 'a' && v <= 'z')
        {
            v -= 32;
        }
        return (sbyte)(byte)v;
    }
}
