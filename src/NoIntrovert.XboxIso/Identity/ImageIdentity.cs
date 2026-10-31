using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace NoIntrovert.XboxIso.Identity;

/// <summary>Table-driven CRC-32 (IEEE 802.3), as used by Redump/No-Intro DATs. Slicing-by-8: eight table lookups per 8 bytes.</summary>
public sealed class Crc32
{
    private static readonly uint[] Table = BuildTable();
    private uint _crc = 0xFFFFFFFF;

    /// <summary>Eight 256-entry tables back to back; table k advances the CRC over k extra zero bytes.</summary>
    private static uint[] BuildTable()
    {
        var t = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            t[i] = c;
        }
        for (int i = 0; i < 256; i++)
        {
            for (int k = 1; k < 8; k++)
            {
                uint prev = t[(k - 1) * 256 + i];
                t[k * 256 + i] = (prev >> 8) ^ t[prev & 0xFF];
            }
        }
        return t;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        uint crc = _crc;
        var t = Table;
        while (data.Length >= 8)
        {
            uint one = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
            uint two = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            crc = t[7 * 256 + (one & 0xFF)] ^ t[6 * 256 + ((one >> 8) & 0xFF)] ^
                  t[5 * 256 + ((one >> 16) & 0xFF)] ^ t[4 * 256 + (one >> 24)] ^
                  t[3 * 256 + (two & 0xFF)] ^ t[2 * 256 + ((two >> 8) & 0xFF)] ^
                  t[1 * 256 + ((two >> 16) & 0xFF)] ^ t[two >> 24];
            data = data[8..];
        }
        foreach (byte b in data)
        {
            crc = t[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        _crc = crc;
    }

    public uint Value => _crc ^ 0xFFFFFFFF;

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var c = new Crc32();
        c.Append(data);
        return c.Value;
    }
}

/// <summary>Whole-file hashes of an image as stored on disk (what Redump DATs describe).</summary>
public sealed record FileHashes(long Size, string Crc32, string Md5, string Sha1)
{
    /// <summary>Hashes the concatenation of <paramref name="files"/> (split sets are hashed as one file).</summary>
    public static FileHashes Compute(IReadOnlyList<string> files, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        long total = files.Sum(f => new FileInfo(f).Length);
        var crc = new Crc32();
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        // Double-buffered: the next chunk is read while the three hashes run side by side on the current one.
        byte[] reading = ArrayPool<byte>.Shared.Rent(4 << 20);
        byte[] hashingBuffer = ArrayPool<byte>.Shared.Rent(4 << 20);
        Task hashing = Task.CompletedTask;
        long done = 0;
        var throttled = new ThrottledProgress(progress);
        try
        {
            foreach (var file in files)
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                while (true)
                {
                    int n = fs.Read(reading, 0, reading.Length);
                    hashing.GetAwaiter().GetResult();
                    if (n <= 0)
                    {
                        break;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    (reading, hashingBuffer) = (hashingBuffer, reading);
                    byte[] chunk = hashingBuffer;
                    hashing = Task.Run(() => Parallel.Invoke(
                        () => crc.Append(chunk.AsSpan(0, n)),
                        () => md5.AppendData(chunk, 0, n),
                        () => sha1.AppendData(chunk, 0, n)));
                    done += n;
                    throttled.Report(new OperationProgress(OperationStage.Hashing, done, total, file));
                }
            }
        }
        finally
        {
            try
            {
                hashing.Wait();
            }
            catch (AggregateException)
            {
                // Already failing; the buffers just must not be returned while still in use.
            }
            ArrayPool<byte>.Shared.Return(reading);
            ArrayPool<byte>.Shared.Return(hashingBuffer);
        }
        return new FileHashes(
            done,
            crc.Value.ToString("x8"),
            Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant());
    }
}

/// <summary>One ROM line from a DAT file.</summary>
public sealed record DatEntry(string GameName, string RomName, long Size, string? Crc32, string? Md5, string? Sha1);

/// <summary>
/// A Logiqx-XML DAT (the format Redump publishes). Matching is by SHA-1, then MD5, then CRC32+size.
/// Match an image <em>before</em> modifying it: any trimmed or rebuilt image no longer matches.
/// </summary>
public sealed class DatFile
{
    private readonly Dictionary<string, DatEntry> _bySha1 = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DatEntry> _byMd5 = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, long), DatEntry> _byCrc = new();

    public string? Name { get; private set; }
    public string? Version { get; private set; }
    public int Count { get; private set; }

    public static DatFile Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static DatFile Load(Stream stream)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        var doc = XDocument.Load(reader);
        var dat = new DatFile();
        var header = doc.Root?.Element("header");
        dat.Name = header?.Element("name")?.Value;
        dat.Version = header?.Element("version")?.Value;

        foreach (var game in doc.Descendants("game").Concat(doc.Descendants("machine")))
        {
            string gameName = game.Attribute("name")?.Value ?? string.Empty;
            foreach (var rom in game.Elements("rom"))
            {
                long.TryParse(rom.Attribute("size")?.Value, out long size);
                var entry = new DatEntry(
                    gameName,
                    rom.Attribute("name")?.Value ?? string.Empty,
                    size,
                    rom.Attribute("crc")?.Value,
                    rom.Attribute("md5")?.Value,
                    rom.Attribute("sha1")?.Value);
                dat.Add(entry);
            }
        }
        return dat;
    }

    private void Add(DatEntry e)
    {
        Count++;
        if (!string.IsNullOrEmpty(e.Sha1))
        {
            _bySha1.TryAdd(e.Sha1, e);
        }
        if (!string.IsNullOrEmpty(e.Md5))
        {
            _byMd5.TryAdd(e.Md5, e);
        }
        if (!string.IsNullOrEmpty(e.Crc32))
        {
            _byCrc.TryAdd((e.Crc32.ToLowerInvariant(), e.Size), e);
        }
    }

    public DatEntry? Match(FileHashes hashes)
    {
        if (_bySha1.TryGetValue(hashes.Sha1, out var e) || _byMd5.TryGetValue(hashes.Md5, out e) ||
            _byCrc.TryGetValue((hashes.Crc32.ToLowerInvariant(), hashes.Size), out e))
        {
            return e;
        }
        return null;
    }
}
