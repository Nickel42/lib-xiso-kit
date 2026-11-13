using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LibXisoKit.Stfs;

/// <summary>A file or folder in an STFS package.</summary>
public sealed record StfsEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    int StartBlock,
    int BlockCount,
    bool Contiguous,
    int Index,
    int ParentIndex);

/// <summary>The outcome of <see cref="StfsPackage.Verify"/>.</summary>
public sealed record StfsVerification(bool Success, IReadOnlyList<string> Problems, int BlocksChecked);

/// <summary>
/// Read access to an Xbox 360 STFS package (an XBLA game, DLC, a title update, a demo): its header, its files,
/// and a check of every hash in its hash tree. An SVOD package (Games on Demand) opens for its header only;
/// its files are a disc image in the .data folder beside it.
/// </summary>
/// <remarks>
/// Data is stored in 4 KiB blocks after the header. Every 170 data blocks are preceded by a level-0 hash table
/// (SHA-1, status and next block of each); every 170 level-0 tables by a level-1 table, which comes after the
/// first 170 data blocks; and so on. A package that keeps two copies of each table (block separation bit 0
/// clear) says which copy is current in the parent entry's status (0x40) or, for the top table, in block
/// separation bit 1. Thread-safe for concurrent reads once opened.
/// </remarks>
public sealed class StfsPackage : IDisposable
{
    public const int BlockSize = 0x1000;
    private const int EntriesPerTable = 170;
    private const int Level1Span = EntriesPerTable * EntriesPerTable; // 28,900 blocks
    private const int Level2Span = Level1Span * EntriesPerTable;
    private const int HashEntrySize = 0x18;
    private const int FileEntrySize = 0x40;
    private const int MaxEntries = 100_000;

    private readonly FileStream _stream;
    private readonly object _gate = new();
    private readonly List<StfsEntry> _entries = new();
    private readonly List<string> _warnings = new();
    private readonly long _firstTable;
    private int _shift;

    private StfsPackage(FileStream stream, StfsHeader header)
    {
        _stream = stream;
        Header = header;
        _firstTable = (header.HeaderSize + 0xFFFL) & ~0xFFFL;
        if (header.VolumeKind != StfsVolumeKind.Stfs)
        {
            return;
        }
        if (header.AllocatedBlockCount > Level2Span)
        {
            throw new InvalidDataException($"Implausible block count {header.AllocatedBlockCount}.");
        }
        _shift = (~header.BlockSeparation) & 1;
        if (!TopTableMatches())
        {
            _shift ^= 1;
            if (!TopTableMatches())
            {
                _shift ^= 1;
                _warnings.Add("The top hash table doesn't match the header's hash; the package may be damaged.");
            }
        }
        ReadFileTable();
    }

    public StfsHeader Header { get; }

    /// <summary>Every file and folder, in file-table order.</summary>
    public IReadOnlyList<StfsEntry> Entries => _entries;

    /// <summary>Things that looked wrong while opening, but didn't stop it.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>How many hash tables each level keeps: 1, or 2 when the console keeps a backup copy.</summary>
    public int TableCopies => 1 << _shift;

    /// <summary>The total size of the files.</summary>
    public long TotalFileBytes => _entries.Where(e => !e.IsDirectory).Sum(e => e.Size);

    /// <summary>Opens the package at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">It isn't an Xbox 360 package, or its file table is damaged.</exception>
    public static StfsPackage Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try
        {
            if (stream.Length < StfsHeader.MinimumLength)
            {
                throw new InvalidDataException("Too short for an Xbox 360 package.");
            }
            var buffer = new byte[StfsHeader.MinimumLength];
            stream.ReadExactly(buffer);
            return new StfsPackage(stream, StfsHeader.Read(buffer));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public IEnumerable<StfsEntry> EnumerateFiles() => _entries.Where(e => !e.IsDirectory);

    /// <summary>The entry at <paramref name="path"/> ("folder/file", either slash, any case), or null.</summary>
    public StfsEntry? Find(string path)
    {
        var wanted = path.Replace('\\', '/').Trim('/');
        return _entries.FirstOrDefault(e => string.Equals(e.Path, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads a whole file into memory.</summary>
    public byte[] ReadAllBytes(StfsEntry file)
    {
        using var output = new MemoryStream(checked((int)file.Size));
        CopyFile(file, output);
        return output.ToArray();
    }

    /// <summary>Writes a file's data to <paramref name="destination"/>.</summary>
    public void CopyFile(StfsEntry file, Stream destination, Action<long>? bytesCopied = null, CancellationToken cancellationToken = default)
    {
        if (file.IsDirectory)
        {
            throw new ArgumentException("Entry is a directory.", nameof(file));
        }
        var block = new byte[BlockSize];
        long left = file.Size;
        foreach (var number in BlocksOf(file))
        {
            if (left <= 0)
            {
                break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            ReadBlock(number, block);
            int n = (int)Math.Min(left, BlockSize);
            destination.Write(block, 0, n);
            left -= n;
            bytesCopied?.Invoke(n);
        }
        if (left > 0)
        {
            throw new InvalidDataException($"{file.Path} is shorter than its size says.");
        }
    }

    /// <summary>Extracts every file and folder to <paramref name="destination"/>.</summary>
    public void ExtractTo(string destination, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        Directory.CreateDirectory(root);
        long done = 0;
        foreach (var e in _entries)
        {
            string target = Path.GetFullPath(Path.Combine(root, e.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Refusing to extract outside the destination: {e.Path}");
            }
            if (e.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            CopyFile(e, output, n =>
            {
                done += n;
                progress?.Report(new OperationProgress(OperationStage.Extracting, done, TotalFileBytes, e.Path));
            }, cancellationToken);
        }
    }

    /// <summary>
    /// Checks the whole hash tree: the top table against the header, every table against its parent's entry, and
    /// every allocated data block against its level-0 entry. The signature over the header isn't checked.
    /// </summary>
    public StfsVerification Verify(IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Header.VolumeKind != StfsVolumeKind.Stfs)
        {
            throw new NotSupportedException("Only STFS packages can be verified; this one is SVOD (Games on Demand).");
        }
        var problems = new List<string>();
        if (!TopTableMatches())
        {
            problems.Add("The top hash table doesn't match the header.");
        }
        int blocks = (int)Header.AllocatedBlockCount;
        int top = TopLevel;
        var buffer = new byte[BlockSize];
        var table = new byte[BlockSize];

        // Tables at each level below the top, against their parent's entry.
        for (int level = top - 1; level >= 0; level--)
        {
            int span = level == 0 ? EntriesPerTable : Level1Span;
            for (int i = 0; i * span < blocks; i++)
            {
                ReadAt(TableAddress(i * span, level), table);
                var expected = HashEntry(i * span, level + 1);
                if (!SHA1.HashData(table).AsSpan().SequenceEqual(expected[..20]))
                {
                    problems.Add($"Level {level} hash table {i} doesn't match its parent.");
                }
            }
        }

        // Data blocks against their level-0 entries.
        for (int b = 0; b < blocks; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadBlock(b, buffer);
            if (!SHA1.HashData(buffer).AsSpan().SequenceEqual(HashEntry(b, 0)[..20]) && problems.Count < 100)
            {
                problems.Add($"Block {b} doesn't match its hash.");
            }
            progress?.Report(new OperationProgress(OperationStage.Verifying, b + 1, blocks));
        }
        return new StfsVerification(problems.Count == 0, problems, blocks);
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>0, 1 or 2: the level of the top hash table, by how many blocks are allocated.</summary>
    private int TopLevel => Header.AllocatedBlockCount <= EntriesPerTable ? 0 : Header.AllocatedBlockCount <= Level1Span ? 1 : 2;

    private bool TopTableMatches()
    {
        var table = new byte[BlockSize];
        return ReadAt(TopTableAddress(), table, throwOnShort: false) && SHA1.HashData(table).AsSpan().SequenceEqual(Header.TopHashTableHash);
    }

    private long TopTableAddress()
    {
        long address = BlockAddress(TableBlock(0, TopLevel));
        return _shift == 1 ? address + ((Header.BlockSeparation & 2) << 11) : address;
    }

    /// <summary>The address of the current copy of the level-<paramref name="level"/> table covering data block <paramref name="block"/>.</summary>
    private long TableAddress(int block, int level)
    {
        if (level == TopLevel)
        {
            return TopTableAddress();
        }
        long address = BlockAddress(TableBlock(block, level));
        if (_shift == 1 && (HashEntry(block, level + 1)[20] & 0x40) != 0)
        {
            address += BlockSize;
        }
        return address;
    }

    /// <summary>The 24-byte entry for data block <paramref name="block"/> (level 0), or for its table at a lower level.</summary>
    private byte[] HashEntry(int block, int level)
    {
        int index = level switch
        {
            0 => block % EntriesPerTable,
            1 => block / EntriesPerTable % EntriesPerTable,
            _ => block / Level1Span % EntriesPerTable,
        };
        var entry = new byte[HashEntrySize];
        ReadAt(TableAddress(block, level) + (long)index * HashEntrySize, entry);
        return entry;
    }

    /// <summary>The physical block number of the first copy of the level-<paramref name="level"/> table covering <paramref name="block"/>.</summary>
    private int TableBlock(int block, int level)
    {
        int copies = 1 << _shift;
        int step0 = EntriesPerTable + copies;              // 170 data blocks and their level-0 table(s)
        int step1 = Level1Span + EntriesPerTable * copies + copies; // ... and a level-1 table
        switch (level)
        {
            case 0:
                if (block < EntriesPerTable)
                {
                    return 0;
                }
                int number = block / EntriesPerTable * step0 + ((block / Level1Span + 1) << _shift);
                return block / Level1Span == 0 ? number : number + copies;
            case 1:
                return block < Level1Span ? step0 : copies + step1 * (block / Level1Span);
            default:
                return step1;
        }
    }

    /// <summary>The physical block number of data block <paramref name="block"/>, past the hash tables before it.</summary>
    private int DataBlock(int block)
    {
        int number = ((block + EntriesPerTable) / EntriesPerTable << _shift) + block;
        if (block < EntriesPerTable)
        {
            return number;
        }
        if (block < Level1Span)
        {
            return number + ((block + Level1Span) / Level1Span << _shift);
        }
        return (1 << _shift) + number + ((block + Level1Span) / Level1Span << _shift);
    }

    private long BlockAddress(int physicalBlock) => _firstTable + (long)physicalBlock * BlockSize;

    private void ReadBlock(int block, byte[] buffer)
    {
        if ((uint)block >= Header.AllocatedBlockCount)
        {
            throw new InvalidDataException($"Block {block} is past the package's {Header.AllocatedBlockCount} blocks.");
        }
        ReadAt(BlockAddress(DataBlock(block)), buffer);
    }

    private bool ReadAt(long position, Span<byte> buffer, bool throwOnShort = true)
    {
        lock (_gate)
        {
            if (position < 0 || position + buffer.Length > _stream.Length)
            {
                return throwOnShort ? throw new InvalidDataException("The package is truncated.") : false;
            }
            _stream.Position = position;
            _stream.ReadExactly(buffer);
            return true;
        }
    }

    /// <summary>The data blocks of a file: consecutive, or chained through the level-0 entries' next-block fields.</summary>
    private IEnumerable<int> BlocksOf(StfsEntry file)
    {
        int block = file.StartBlock;
        for (int i = 0; i < file.BlockCount; i++)
        {
            yield return block;
            block = file.Contiguous ? block + 1 : NextBlock(block);
        }
    }

    private int NextBlock(int block)
    {
        var entry = HashEntry(block, 0);
        return (entry[21] << 16) | (entry[22] << 8) | entry[23];
    }

    private void ReadFileTable()
    {
        var table = new byte[BlockSize];
        int block = Header.FileTableBlockNumber;
        var names = Encoding.Latin1;
        var byPosition = new Dictionary<int, int>(); // a folder's position in the table → its index in _entries
        for (int t = 0; t < Header.FileTableBlockCount; t++)
        {
            ReadBlock(block, table);
            for (int offset = 0; offset < BlockSize; offset += FileEntrySize)
            {
                var e = table.AsSpan(offset, FileEntrySize);
                int flags = e[0x28];
                int nameLength = flags & 0x3F;
                if (nameLength == 0)
                {
                    continue;
                }
                if (_entries.Count >= MaxEntries)
                {
                    throw new InvalidDataException("Too many entries in the file table.");
                }
                string name = names.GetString(e[..Math.Min(nameLength, 0x28)]);
                if (name is "." or ".." || name.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
                {
                    throw new InvalidDataException($"Bad file name in the package: \"{name}\".");
                }
                int parent = BinaryPrimitives.ReadInt16BigEndian(e[0x32..]);
                byPosition[t * (BlockSize / FileEntrySize) + offset / FileEntrySize] = _entries.Count;
                _entries.Add(new StfsEntry(
                    name,
                    name,
                    (flags & 0x80) != 0,
                    BinaryPrimitives.ReadUInt32BigEndian(e[0x34..]),
                    e[0x2F] | (e[0x30] << 8) | (e[0x31] << 16),
                    e[0x29] | (e[0x2A] << 8) | (e[0x2B] << 16),
                    (flags & 0x40) != 0,
                    _entries.Count,
                    parent));
            }
            if (t + 1 < Header.FileTableBlockCount)
            {
                block = NextBlock(block);
            }
        }
        // Parents are recorded by table position; empty slots are skipped, so map them to list indexes.
        for (int i = 0; i < _entries.Count; i++)
        {
            int parent = _entries[i].ParentIndex;
            _entries[i] = _entries[i] with { ParentIndex = parent < 0 ? -1 : byPosition.GetValueOrDefault(parent, int.MaxValue) };
        }
        for (int i = 0; i < _entries.Count; i++)
        {
            _entries[i] = _entries[i] with { Path = PathOf(i) };
        }
    }

    /// <summary>"folder/file", following parent indexes up to the root (-1), refusing loops.</summary>
    private string PathOf(int index)
    {
        var parts = new List<string>();
        int current = index;
        for (int depth = 0; current >= 0; depth++)
        {
            if (depth > 64 || current >= _entries.Count || (current != index && !_entries[current].IsDirectory))
            {
                throw new InvalidDataException($"Bad folder structure in the package at \"{_entries[index].Name}\".");
            }
            parts.Add(_entries[current].Name);
            current = _entries[current].ParentIndex;
        }
        parts.Reverse();
        return string.Join('/', parts);
    }
}
