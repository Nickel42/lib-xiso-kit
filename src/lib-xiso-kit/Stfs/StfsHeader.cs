using System.Buffers.Binary;
using System.Text;

namespace LibXisoKit.Stfs;

/// <summary>Who signed an Xbox 360 package: the magic at offset 0.</summary>
public enum StfsSignature
{
    /// <summary>"CON ": signed by a console (saves, profiles, content made on the console).</summary>
    Con,

    /// <summary>"LIVE": signed by Xbox Live (XBLA games, DLC, demos, Games on Demand).</summary>
    Live,

    /// <summary>"PIRS": signed by Microsoft, not delivered over Live (title updates, system content).</summary>
    Pirs,
}

/// <summary>How a package stores its files.</summary>
public enum StfsVolumeKind
{
    /// <summary>Secure Transacted File System: the files are inside the package.</summary>
    Stfs = 0,

    /// <summary>Secure Virtual Optical Device: a disc image in a "&lt;name&gt;.data" folder beside the header (Games on Demand).</summary>
    Svod = 1,
}

/// <summary>What a package holds (the content type at 0x344).</summary>
public enum XContentType : uint
{
    Unknown = 0,
    SavedGame = 0x1,
    MarketplaceContent = 0x2,
    Publisher = 0x3,
    Xbox360Title = 0x1000,
    IptvPauseBuffer = 0x2000,
    InstalledGame = 0x4000,
    XboxOriginalGame = 0x5000,
    GamesOnDemand = 0x7000,
    AvatarItem = 0x9000,
    Profile = 0x10000,
    GamerPicture = 0x20000,
    Theme = 0x30000,
    CacheFile = 0x40000,
    StorageDownload = 0x50000,
    XboxSavedGame = 0x60000,
    XboxDownload = 0x70000,
    GameDemo = 0x80000,
    Video = 0x90000,
    GameTitle = 0xA0000,
    Installer = 0xB0000,
    GameTrailer = 0xC0000,
    ArcadeTitle = 0xD0000,
    Xna = 0xE0000,
    LicenseStore = 0xF0000,
    Movie = 0x100000,
    Tv = 0x200000,
    MusicVideo = 0x300000,
    GameVideo = 0x400000,
    PodcastVideo = 0x500000,
    ViralVideo = 0x600000,
    CommunityGame = 0x2000000,
}

/// <summary>
/// The metadata at the front of an Xbox 360 package (CON, LIVE or PIRS): what it is, which title it belongs to
/// and, for STFS packages, where its file table is. All integers are big-endian except the file table's
/// block count and number in the volume descriptor, which are little-endian.
/// </summary>
public sealed class StfsHeader
{
    /// <summary>The bytes <see cref="Read(ReadOnlySpan{byte})"/> needs: everything up to the title name.</summary>
    public const int MinimumLength = 0x1711;

    internal const int VolumeDescriptorOffset = 0x379;
    private const int LocaleNameLength = 0x80;

    public StfsSignature Signature { get; private init; }

    /// <summary>The size of the header in bytes; the first hash table follows it on a 4 KiB boundary.</summary>
    public uint HeaderSize { get; private init; }

    public XContentType ContentType { get; private init; }

    public uint MetadataVersion { get; private init; }

    public ulong ContentSize { get; private init; }

    public uint MediaId { get; private init; }

    /// <summary>The content's version, as stored (packed major.minor.build.revision).</summary>
    public uint Version { get; private init; }

    public uint BaseVersion { get; private init; }

    public uint TitleId { get; private init; }

    /// <summary>The title ID as the eight hex digits it's usually written as ("4D5307E6").</summary>
    public string TitleIdText => TitleId.ToString("X8");

    public byte Platform { get; private init; }

    public byte ExecutableType { get; private init; }

    public byte DiscNumber { get; private init; }

    public byte DiscsInSet { get; private init; }

    public StfsVolumeKind VolumeKind { get; private init; }

    /// <summary>For an SVOD package (Games on Demand): how many Data#### files its .data folder holds.</summary>
    public uint DataFileCount { get; private init; }

    /// <summary>For an SVOD package: the total size of its Data#### files.</summary>
    public ulong DataFileCombinedSize { get; private init; }

    /// <summary>The English display name ("Geometry Wars: Retro Evolved"), or empty.</summary>
    public string DisplayName { get; private init; } = "";

    public string Description { get; private init; } = "";

    public string PublisherName { get; private init; } = "";

    public string TitleName { get; private init; } = "";

    /// <summary>The STFS volume descriptor's block separation byte: bit 0 set means one copy of each hash table.</summary>
    public byte BlockSeparation { get; private init; }

    public int FileTableBlockCount { get; private init; }

    public int FileTableBlockNumber { get; private init; }

    /// <summary>SHA-1 of the top hash table.</summary>
    public byte[] TopHashTableHash { get; private init; } = [];

    public uint AllocatedBlockCount { get; private init; }

    public uint UnallocatedBlockCount { get; private init; }

    /// <summary>A plain description of <see cref="ContentType"/>: "Xbox Live Arcade game", "Title update"…</summary>
    public string ContentTypeText => ContentType switch
    {
        XContentType.ArcadeTitle => "Xbox Live Arcade game",
        XContentType.MarketplaceContent => "Downloadable content",
        XContentType.Installer => "Title update",
        XContentType.GamesOnDemand => "Games on Demand",
        XContentType.InstalledGame => "Installed game",
        XContentType.XboxOriginalGame => "Original Xbox game",
        XContentType.GameDemo => "Game demo",
        XContentType.CommunityGame => "Indie game",
        XContentType.Xbox360Title => "Xbox 360 title",
        XContentType.SavedGame => "Saved game",
        XContentType.Theme => "Theme",
        XContentType.GamerPicture => "Gamer picture",
        XContentType.AvatarItem => "Avatar item",
        XContentType.GameTrailer or XContentType.GameVideo or XContentType.Video => "Video",
        _ => $"Content type 0x{(uint)ContentType:X}",
    };

    /// <summary>The signature a file starts with, or null when it isn't an Xbox 360 package.</summary>
    public static StfsSignature? SignatureOf(ReadOnlySpan<byte> start)
    {
        if (start.Length < 4)
        {
            return null;
        }
        if (start[..4].SequenceEqual("CON "u8))
        {
            return StfsSignature.Con;
        }
        if (start[..4].SequenceEqual("LIVE"u8))
        {
            return StfsSignature.Live;
        }
        if (start[..4].SequenceEqual("PIRS"u8))
        {
            return StfsSignature.Pirs;
        }
        return null;
    }

    /// <summary>Whether <paramref name="path"/> is an Xbox 360 package: a known signature and a header that reads.</summary>
    public static bool IsPackage(string path) => TryRead(path) is not null;

    /// <summary>The header of the package at <paramref name="path"/>, or null when it isn't one (or can't be read).</summary>
    public static StfsHeader? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            if (stream.Length < MinimumLength)
            {
                return null;
            }
            var buffer = new byte[MinimumLength];
            stream.ReadExactly(buffer);
            return SignatureOf(buffer) is null ? null : Read(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Reads a header from the first <see cref="MinimumLength"/> bytes of a package.</summary>
    /// <exception cref="InvalidDataException">The bytes aren't an Xbox 360 package header.</exception>
    public static StfsHeader Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinimumLength)
        {
            throw new InvalidDataException("Too short for an Xbox 360 package header.");
        }
        var signature = SignatureOf(data) ?? throw new InvalidDataException("Not an Xbox 360 package (no CON, LIVE or PIRS signature).");
        var headerSize = BinaryPrimitives.ReadUInt32BigEndian(data[0x340..]);
        if (headerSize < MinimumLength || headerSize > 0x100000)
        {
            throw new InvalidDataException($"Implausible package header size 0x{headerSize:X}.");
        }
        var kind = BinaryPrimitives.ReadUInt32BigEndian(data[0x3A9..]);
        if (kind > 1)
        {
            throw new InvalidDataException($"Unknown package volume type {kind}.");
        }

        var vd = data.Slice(VolumeDescriptorOffset, 0x24);
        return new StfsHeader
        {
            Signature = signature,
            HeaderSize = headerSize,
            ContentType = (XContentType)BinaryPrimitives.ReadUInt32BigEndian(data[0x344..]),
            MetadataVersion = BinaryPrimitives.ReadUInt32BigEndian(data[0x348..]),
            ContentSize = BinaryPrimitives.ReadUInt64BigEndian(data[0x34C..]),
            MediaId = BinaryPrimitives.ReadUInt32BigEndian(data[0x354..]),
            Version = BinaryPrimitives.ReadUInt32BigEndian(data[0x358..]),
            BaseVersion = BinaryPrimitives.ReadUInt32BigEndian(data[0x35C..]),
            TitleId = BinaryPrimitives.ReadUInt32BigEndian(data[0x360..]),
            Platform = data[0x364],
            ExecutableType = data[0x365],
            DiscNumber = data[0x366],
            DiscsInSet = data[0x367],
            VolumeKind = (StfsVolumeKind)kind,
            DataFileCount = BinaryPrimitives.ReadUInt32BigEndian(data[0x39D..]),
            DataFileCombinedSize = BinaryPrimitives.ReadUInt64BigEndian(data[0x3A1..]),
            DisplayName = Utf16(data.Slice(0x411, LocaleNameLength)),
            Description = Utf16(data.Slice(0xD11, LocaleNameLength)),
            PublisherName = Utf16(data.Slice(0x1611, LocaleNameLength)),
            TitleName = Utf16(data.Slice(0x1691, LocaleNameLength)),
            BlockSeparation = kind == 0 ? vd[2] : (byte)0,
            FileTableBlockCount = kind == 0 ? BinaryPrimitives.ReadUInt16LittleEndian(vd[3..]) : 0,
            FileTableBlockNumber = kind == 0 ? vd[5] | (vd[6] << 8) | (vd[7] << 16) : 0,
            TopHashTableHash = kind == 0 ? vd.Slice(8, 20).ToArray() : [],
            AllocatedBlockCount = kind == 0 ? BinaryPrimitives.ReadUInt32BigEndian(vd[0x1C..]) : 0,
            UnallocatedBlockCount = kind == 0 ? BinaryPrimitives.ReadUInt32BigEndian(vd[0x20..]) : 0,
        };
    }

    /// <summary>A NUL-terminated big-endian UTF-16 string.</summary>
    private static string Utf16(ReadOnlySpan<byte> field)
    {
        int length = 0;
        while (length + 1 < field.Length && (field[length] | field[length + 1]) != 0)
        {
            length += 2;
        }
        return Encoding.BigEndianUnicode.GetString(field[..length]).Trim();
    }
}
