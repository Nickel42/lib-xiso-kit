using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using NoIntrovert.XboxIso.IO;

namespace NoIntrovert.XboxIso.Xdvdfs;

/// <summary>A file or directory in an XDVDFS volume.</summary>
public sealed class XdvdfsEntry
{
    internal readonly List<XdvdfsEntry> ChildList = new();

    internal XdvdfsEntry(string name, string path, byte attributes, uint startSector, uint size, XdvdfsEntry? parent)
    {
        Name = name;
        Path = path;
        Attributes = attributes;
        StartSector = startSector;
        Size = size;
        Parent = parent;
    }

    /// <summary>Entry name as stored on disc.</summary>
    public string Name { get; }

    /// <summary>Absolute path inside the image, '/'-separated, starting with '/'. The root is "/".</summary>
    public string Path { get; }

    public byte Attributes { get; }

    public bool IsDirectory => (Attributes & XdvdfsConstants.AttributeDirectory) != 0;

    /// <summary>Partition-relative start sector of the file data or directory table.</summary>
    public uint StartSector { get; }

    /// <summary>File size, or directory table size, in bytes.</summary>
    public uint Size { get; }

    public XdvdfsEntry? Parent { get; }

    /// <summary>Children in on-disc (sorted) order. Empty for files.</summary>
    public IReadOnlyList<XdvdfsEntry> Children => ChildList;

    public long SectorCount => XdvdfsConstants.SectorsFor(Size);

    public override string ToString() => Path;
}

/// <summary>
/// Read access to the XDVDFS filesystem inside any supported image (Redump, XISO, CCI, CSO, split sets).
/// Thread-safe for concurrent reads once opened.
/// </summary>
public sealed class XdvdfsVolume : IDisposable
{
    private const int MaxEntries = 1_000_000;
    private const int MaxDepth = 64;

    private readonly bool _ownsSource;
    private readonly List<string> _warnings = new();

    private XdvdfsVolume(ImageSource source, bool ownsSource, long partitionOffset, DiscLayout layout)
    {
        Source = source;
        _ownsSource = ownsSource;
        PartitionOffset = partitionOffset;
        Layout = layout;
        PartitionLength = source.Length - partitionOffset;
        Root = new XdvdfsEntry(string.Empty, "/", XdvdfsConstants.AttributeDirectory, 0, 0, null);
    }

    /// <summary>The underlying logical image.</summary>
    public ImageSource Source { get; }

    /// <summary>Byte offset of the game partition within <see cref="Source"/>.</summary>
    public long PartitionOffset { get; }

    /// <summary>Bytes from the start of the game partition to the end of the image.</summary>
    public long PartitionLength { get; }

    public DiscLayout Layout { get; }

    public uint RootSector { get; private set; }

    public uint RootSize { get; private set; }

    /// <summary>Raw Windows FILETIME from the volume descriptor.</summary>
    public ulong FileTimeRaw { get; private set; }

    /// <summary>Volume creation time, or null when the descriptor stores 0.</summary>
    public DateTimeOffset? CreationTime =>
        FileTimeRaw == 0 || FileTimeRaw > (ulong)DateTime.MaxValue.ToFileTimeUtc() ? null : DateTimeOffset.FromFileTime((long)FileTimeRaw).ToUniversalTime();

    /// <summary>True when the image carries the "in!xiso" optimized tag written by XISO tools.</summary>
    public bool IsOptimized { get; private set; }

    /// <summary>The optimized tag text, when present.</summary>
    public string? OptimizedTag { get; private set; }

    public XdvdfsEntry Root { get; }

    public int FileCount { get; private set; }

    public int DirectoryCount { get; private set; }

    /// <summary>Total bytes of file data (before deduplication).</summary>
    public long TotalFileBytes { get; private set; }

    /// <summary>One past the highest partition sector referenced by the filesystem.</summary>
    public long UsedEndSector { get; private set; }

    /// <summary>Non-fatal oddities found while reading the tree.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Opens an image file of any supported container.</summary>
    public static XdvdfsVolume Open(string path)
    {
        var source = ImageOpener.Open(path);
        try
        {
            return Open(source, ownsSource: true);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Opens the XDVDFS volume inside an existing source.</summary>
    public static XdvdfsVolume Open(ImageSource source, bool ownsSource)
    {
        var (offset, layout) = DetectPartition(source)
            ?? throw new InvalidDataException("No Xbox filesystem (MICROSOFT*XBOX*MEDIA) was found in this image.");
        var volume = new XdvdfsVolume(source, ownsSource, offset, layout);
        volume.ReadDescriptor();
        volume.ReadTree();
        return volume;
    }

    /// <summary>Returns true if <paramref name="source"/> contains a recognisable XDVDFS volume.</summary>
    public static bool IsXdvdfs(ImageSource source) => DetectPartition(source) is not null;

    internal static (long Offset, DiscLayout Layout)? DetectPartition(ImageSource source)
    {
        (long, DiscLayout)[] candidates =
        {
            (0, DiscLayout.Xiso),
            (XdvdfsConstants.Xgd1PartitionOffset, DiscLayout.RedumpXgd1),
            (XdvdfsConstants.Xgd2PartitionOffset, DiscLayout.Xgd2),
            (XdvdfsConstants.Xgd3PartitionOffset, DiscLayout.Xgd3),
            (XdvdfsConstants.Xgd2HybridPartitionOffset, DiscLayout.Xgd2Hybrid),
        };
        Span<byte> buf = stackalloc byte[20];
        foreach (var (offset, layout) in candidates)
        {
            long pos = offset + XdvdfsConstants.DescriptorOffset;
            if (pos + 20 > source.Length)
            {
                continue;
            }
            if (source.Read(pos, buf) == 20 && buf.SequenceEqual(XdvdfsConstants.Magic))
            {
                return (offset, layout);
            }
        }
        return null;
    }

    private void ReadDescriptor()
    {
        var d = new byte[XdvdfsConstants.SectorSize];
        Source.ReadExactly(PartitionOffset + XdvdfsConstants.DescriptorOffset, d);
        if (!d.AsSpan(0x7EC, 20).SequenceEqual(XdvdfsConstants.Magic))
        {
            _warnings.Add("Volume descriptor trailing magic is missing; image may be corrupt.");
        }
        RootSector = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(20));
        RootSize = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(24));
        FileTimeRaw = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(28));

        var tag = new byte[24];
        if (Source.Read(PartitionOffset + XdvdfsConstants.OptimizedTagOffset, tag) == tag.Length &&
            tag.AsSpan(0, 7).SequenceEqual("in!xiso"u8))
        {
            IsOptimized = true;
            OptimizedTag = Encoding.ASCII.GetString(tag).TrimEnd('\0', ' ');
        }
    }

    private void ReadTree()
    {
        UsedEndSector = XdvdfsConstants.DescriptorSector + 1;
        if (RootSize == 0)
        {
            return; // empty image
        }

        var visitedTables = new HashSet<uint>();
        var stack = new Stack<(XdvdfsEntry Dir, uint Sector, uint Size, int Depth)>();
        stack.Push((Root, RootSector, RootSize, 0));
        int entries = 0;

        while (stack.Count > 0)
        {
            var (dir, sector, size, depth) = stack.Pop();
            if (depth > MaxDepth)
            {
                throw new InvalidDataException($"Directory nesting deeper than {MaxDepth} at {dir.Path}.");
            }
            if (!visitedTables.Add(sector))
            {
                throw new InvalidDataException($"Directory table at sector {sector} is referenced twice (cycle) at {dir.Path}.");
            }

            long tableBytes = XdvdfsConstants.SectorsFor(size) * XdvdfsConstants.SectorSize;
            CheckBounds(sector, (uint)Math.Min(tableBytes, uint.MaxValue), dir.Path);
            Touch(sector, size);

            var table = new byte[tableBytes];
            int read = Source.Read(PartitionOffset + (long)sector * XdvdfsConstants.SectorSize, table);
            if (read < size)
            {
                throw new InvalidDataException($"Directory table for {dir.Path} is truncated.");
            }

            ParseTable(dir, table, (int)Math.Min(size, (uint)table.Length), ref entries);

            // Push in reverse so directories are processed in on-disc order.
            for (int i = dir.ChildList.Count - 1; i >= 0; i--)
            {
                var c = dir.ChildList[i];
                if (c.IsDirectory)
                {
                    DirectoryCount++;
                    if (c.Size > 0)
                    {
                        stack.Push((c, c.StartSector, c.Size, depth + 1));
                    }
                }
            }
        }
    }

    private void ParseTable(XdvdfsEntry dir, byte[] table, int size, ref int entries)
    {
        // Directory tables are binary search trees; an in-order walk yields sorted names.
        if (size < XdvdfsConstants.EntryHeaderSize ||
            BinaryPrimitives.ReadUInt16LittleEndian(table) == 0xFFFF)
        {
            return; // empty directory
        }

        var seen = new HashSet<int>();
        var stack = new Stack<(int Offset, bool Expanded)>();
        stack.Push((0, false));
        while (stack.Count > 0)
        {
            var (off, expanded) = stack.Pop();
            if (off < 0 || off + XdvdfsConstants.EntryHeaderSize > size)
            {
                throw new InvalidDataException($"Directory entry offset 0x{off:X} is outside the table of {dir.Path}.");
            }

            var e = table.AsSpan(off);
            ushort left = BinaryPrimitives.ReadUInt16LittleEndian(e);
            ushort right = BinaryPrimitives.ReadUInt16LittleEndian(e[2..]);
            if (left == 0xFFFF)
            {
                throw new InvalidDataException($"Directory entry at 0x{off:X} in {dir.Path} is padding.");
            }

            if (!expanded)
            {
                if (!seen.Add(off))
                {
                    throw new InvalidDataException($"Cycle in directory table of {dir.Path}.");
                }
                if (right != 0)
                {
                    stack.Push((right * 4, false));
                }
                stack.Push((off, true));
                if (left != 0)
                {
                    stack.Push((left * 4, false));
                }
                continue;
            }

            if (++entries > MaxEntries)
            {
                throw new InvalidDataException("Too many directory entries; image is likely corrupt.");
            }

            uint start = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            byte attributes = e[12];
            int nameLength = e[13];
            if (off + XdvdfsConstants.EntryHeaderSize + nameLength > size || nameLength == 0)
            {
                throw new InvalidDataException($"Bad file name length in {dir.Path}.");
            }
            string name = Encoding.Latin1.GetString(e.Slice(XdvdfsConstants.EntryHeaderSize, nameLength));
            if (name is "." or ".." || name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
            {
                throw new InvalidDataException($"Unsafe file name \"{name}\" in {dir.Path}.");
            }

            string path = dir.Path == "/" ? "/" + name : dir.Path + "/" + name;
            var entry = new XdvdfsEntry(name, path, attributes, start, length, dir);
            dir.ChildList.Add(entry);

            if (!entry.IsDirectory)
            {
                FileCount++;
                TotalFileBytes += length;
                if (length > 0)
                {
                    if (!CheckBounds(start, length, path, throwOnError: false))
                    {
                        _warnings.Add($"{path} extends past the end of the image.");
                    }
                    Touch(start, length);
                }
            }
        }
    }

    private bool CheckBounds(uint sector, uint length, string path, bool throwOnError = true)
    {
        long end = (long)sector * XdvdfsConstants.SectorSize + length;
        if (end <= PartitionLength)
        {
            return true;
        }
        if (throwOnError)
        {
            throw new InvalidDataException($"{path} points past the end of the image (sector {sector}).");
        }
        return false;
    }

    private void Touch(uint sector, uint length)
    {
        long end = sector + XdvdfsConstants.SectorsFor(length);
        if (end > UsedEndSector)
        {
            UsedEndSector = end;
        }
    }

    /// <summary>Every entry (files and directories) in depth-first, on-disc order. Excludes the root.</summary>
    public IEnumerable<XdvdfsEntry> EnumerateEntries()
    {
        var stack = new Stack<XdvdfsEntry>();
        for (int i = Root.ChildList.Count - 1; i >= 0; i--)
        {
            stack.Push(Root.ChildList[i]);
        }
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            for (int i = e.ChildList.Count - 1; i >= 0; i--)
            {
                stack.Push(e.ChildList[i]);
            }
        }
    }

    /// <summary>Every file (not directory).</summary>
    public IEnumerable<XdvdfsEntry> EnumerateFiles() => EnumerateEntries().Where(e => !e.IsDirectory);

    /// <summary>Finds an entry by path (case-insensitive, '/' or '\' separators). Returns null when missing.</summary>
    public XdvdfsEntry? Find(string path)
    {
        var current = Root;
        foreach (var part in path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            XdvdfsEntry? next = null;
            foreach (var c in current.ChildList)
            {
                if (XdvdfsNameComparer.Instance.Compare(c.Name, part) == 0)
                {
                    next = c;
                    break;
                }
            }
            if (next is null)
            {
                return null;
            }
            current = next;
        }
        return current;
    }

    /// <summary>Reads file data starting at <paramref name="offset"/>; clamped to the file size.</summary>
    public int ReadFile(XdvdfsEntry file, long offset, Span<byte> buffer)
    {
        if (file.IsDirectory)
        {
            throw new ArgumentException("Entry is a directory.", nameof(file));
        }
        if (offset >= file.Size)
        {
            return 0;
        }
        int want = (int)Math.Min(buffer.Length, file.Size - offset);
        return Source.Read(PartitionOffset + (long)file.StartSector * XdvdfsConstants.SectorSize + offset, buffer[..want]);
    }

    /// <summary>Reads a whole file into memory.</summary>
    public byte[] ReadAllBytes(XdvdfsEntry file)
    {
        var data = new byte[file.Size];
        int n = ReadFile(file, 0, data);
        if (n != data.Length)
        {
            throw new EndOfStreamException($"{file.Path} is truncated ({n} of {data.Length} bytes).");
        }
        return data;
    }

    /// <summary>Opens a read-only stream over a file's data.</summary>
    public Stream OpenFile(XdvdfsEntry file)
    {
        if (file.IsDirectory)
        {
            throw new ArgumentException("Entry is a directory.", nameof(file));
        }
        long start = PartitionOffset + (long)file.StartSector * XdvdfsConstants.SectorSize;
        return new SubrangeSource(Source, start, file.Size).AsStream();
    }

    /// <summary>Extracts every file and directory to <paramref name="destination"/>.</summary>
    public void ExtractTo(string destination, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        long done = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            foreach (var e in EnumerateEntries())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.GetFullPath(Path.Combine(root, e.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.Ordinal))
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
                long offset = 0;
                while (offset < e.Size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = ReadFile(e, offset, buffer);
                    if (n <= 0)
                    {
                        throw new EndOfStreamException($"{e.Path} is truncated.");
                    }
                    output.Write(buffer, 0, n);
                    offset += n;
                    done += n;
                    progress?.Report(new OperationProgress(OperationStage.Extracting, done, TotalFileBytes, e.Path));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (_ownsSource)
        {
            Source.Dispose();
        }
    }
}
