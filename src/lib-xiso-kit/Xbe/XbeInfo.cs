using System.Buffers.Binary;
using System.Text;

namespace LibXisoKit.Xbe;

/// <summary>Region flags from the XBE certificate.</summary>
[Flags]
public enum XbeRegion : uint
{
    None = 0,
    NorthAmerica = 0x1,
    Japan = 0x2,
    RestOfWorld = 0x4,
    Manufacturing = 0x80000000,
}

/// <summary>Identity fields from an original Xbox executable's header and certificate.</summary>
public sealed record XbeInfo
{
    public required uint BaseAddress { get; init; }
    public required uint CertificateTimestamp { get; init; }
    public required uint TitleId { get; init; }
    public required string TitleName { get; init; }
    public required uint AllowedMedia { get; init; }
    public required XbeRegion Region { get; init; }
    public required uint Ratings { get; init; }
    public required uint DiscNumber { get; init; }
    public required uint Version { get; init; }
    public required uint SectionCount { get; init; }

    /// <summary>Title ID as eight hex digits, e.g. "4D530004".</summary>
    public string TitleIdHex => TitleId.ToString("X8");

    /// <summary>Publisher-style title ID, e.g. "MS-004", when the publisher bytes are printable.</summary>
    public string TitleIdDisplay
    {
        get
        {
            char a = (char)((TitleId >> 24) & 0xFF);
            char b = (char)((TitleId >> 16) & 0xFF);
            return char.IsAsciiLetterOrDigit(a) && char.IsAsciiLetterOrDigit(b)
                ? $"{a}{b}-{TitleId & 0xFFFF:D3}"
                : TitleIdHex;
        }
    }

    /// <summary>Short region label in the style used by Xbox ISO managers (GLO, USA, PAL, JPN, USA-PAL, ...).</summary>
    public string RegionLabel
    {
        get
        {
            bool na = Region.HasFlag(XbeRegion.NorthAmerica);
            bool jp = Region.HasFlag(XbeRegion.Japan);
            bool row = Region.HasFlag(XbeRegion.RestOfWorld);
            return (na, jp, row) switch
            {
                (true, true, true) => "GLO",
                (true, false, false) => "USA",
                (false, true, false) => "JPN",
                (false, false, true) => "PAL",
                (true, true, false) => "USA-JPN",
                (true, false, true) => "USA-PAL",
                (false, true, true) => "JPN-PAL",
                _ => "UNK",
            };
        }
    }
}

/// <summary>Parses XBE headers.</summary>
public static class XbeReader
{
    private const int CertificateMinSize = 0xB0;

    /// <summary>Returns true when <paramref name="data"/> starts with the "XBEH" magic.</summary>
    public static bool IsXbe(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual("XBEH"u8);

    /// <summary>
    /// Parses the header and certificate. <paramref name="data"/> must contain at least the XBE headers
    /// (the first 64 KiB is always enough for retail executables). Returns null when it is not a valid XBE.
    /// </summary>
    public static XbeInfo? TryParse(ReadOnlySpan<byte> data)
    {
        if (!IsXbe(data) || data.Length < 0x178)
        {
            return null;
        }

        uint baseAddress = BinaryPrimitives.ReadUInt32LittleEndian(data[0x104..]);
        uint certAddress = BinaryPrimitives.ReadUInt32LittleEndian(data[0x118..]);
        uint sections = BinaryPrimitives.ReadUInt32LittleEndian(data[0x11C..]);
        if (certAddress < baseAddress)
        {
            return null;
        }
        long certOffset = (long)certAddress - baseAddress;
        if (certOffset + CertificateMinSize > data.Length)
        {
            return null;
        }

        var cert = data.Slice((int)certOffset);
        string name = Encoding.Unicode.GetString(cert.Slice(0x0C, 80));
        int nul = name.IndexOf('\0');
        if (nul >= 0)
        {
            name = name[..nul];
        }

        return new XbeInfo
        {
            BaseAddress = baseAddress,
            CertificateTimestamp = BinaryPrimitives.ReadUInt32LittleEndian(cert[0x04..]),
            TitleId = BinaryPrimitives.ReadUInt32LittleEndian(cert[0x08..]),
            TitleName = name.Trim(),
            AllowedMedia = BinaryPrimitives.ReadUInt32LittleEndian(cert[0x9C..]),
            Region = (XbeRegion)BinaryPrimitives.ReadUInt32LittleEndian(cert[0xA0..]),
            Ratings = BinaryPrimitives.ReadUInt32LittleEndian(cert[0xA4..]),
            DiscNumber = BinaryPrimitives.ReadUInt32LittleEndian(cert[0xA8..]),
            Version = BinaryPrimitives.ReadUInt32LittleEndian(cert[0xAC..]),
            SectionCount = sections,
        };
    }
}

/// <summary>
/// The media-check patch applied by extract-xiso to <c>.xbe</c> files so games run from hard disk
/// on BIOSes that do not patch it themselves. The patch is idempotent.
/// </summary>
public static class XbeMediaPatch
{
    /// <summary>Byte sequence searched for; the byte at <see cref="PatchIndex"/> (a JGE) becomes a JMP.</summary>
    public static ReadOnlySpan<byte> Pattern => new byte[] { 0xE8, 0xCA, 0xFD, 0xFF, 0xFF, 0x85, 0xC0, 0x7D };

    public const int PatchIndex = 7;
    public const byte PatchedByte = 0xEB;

    /// <summary>Applies the patch in place and returns the number of locations changed.</summary>
    public static int Apply(Span<byte> data)
    {
        int count = 0;
        int pos = 0;
        var pattern = Pattern;
        while (pos <= data.Length - pattern.Length)
        {
            int idx = data[pos..].IndexOf(pattern);
            if (idx < 0)
            {
                break;
            }
            data[pos + idx + PatchIndex] = PatchedByte;
            count++;
            pos += idx + pattern.Length;
        }
        return count;
    }

    /// <summary>True for file names that the patch applies to.</summary>
    public static bool AppliesTo(string fileName) => fileName.EndsWith(".xbe", StringComparison.OrdinalIgnoreCase);
}
