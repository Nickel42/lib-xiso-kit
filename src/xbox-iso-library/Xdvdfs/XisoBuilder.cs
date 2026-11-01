using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using XboxIsoLibrary.IO;

namespace XboxIsoLibrary.Xdvdfs;

/// <summary>A file or directory to be placed in a rebuilt XISO.</summary>
public sealed class XisoBuildNode
{
    private readonly List<XisoBuildNode> _children = new();

    private XisoBuildNode(string name, bool isDirectory, long size, ContentProvider? content, string? sourcePath)
    {
        if (name.Length > XdvdfsConstants.MaxNameLength)
        {
            throw new ArgumentException($"File name longer than {XdvdfsConstants.MaxNameLength} characters: {name}");
        }
        if (size > uint.MaxValue)
        {
            throw new ArgumentException($"{name} is larger than 4 GiB, which XDVDFS cannot store.");
        }
        Name = name;
        IsDirectory = isDirectory;
        Size = size;
        Content = content;
        SourcePath = sourcePath;
    }

    public string Name { get; }
    public bool IsDirectory { get; }
    public long Size { get; }
    public ContentProvider? Content { get; internal set; }

    /// <summary>Path in the source image or host file system, for reporting.</summary>
    public string? SourcePath { get; }

    public IReadOnlyList<XisoBuildNode> Children => _children;

    // Layout state.
    internal uint Sector;
    internal int TableOffset;
    internal int TableSize;
    internal uint TableAllocation;
    internal XisoBuildNode? Left;
    internal XisoBuildNode? Right;
    internal XisoBuildNode? TreeRoot;
    internal bool IsDuplicate;
    internal (long Size, byte[] Hash)? ContentKey;

    public static XisoBuildNode Directory(string name, string? sourcePath = null) => new(name, true, 0, null, sourcePath);

    public static XisoBuildNode File(string name, long size, ContentProvider content, string? sourcePath = null) =>
        new(name, false, size, content, sourcePath);

    public XisoBuildNode Add(XisoBuildNode child)
    {
        if (!IsDirectory)
        {
            throw new InvalidOperationException("Cannot add children to a file.");
        }
        _children.Add(child);
        return child;
    }

    internal IEnumerable<XisoBuildNode> AllFiles()
    {
        foreach (var c in _children)
        {
            if (c.IsDirectory)
            {
                foreach (var f in c.AllFiles())
                {
                    yield return f;
                }
            }
            else
            {
                yield return c;
            }
        }
    }

    /// <summary>Builds a tree from a folder on disk.</summary>
    public static XisoBuildNode FromDirectory(string path, Func<string, bool>? exclude = null)
    {
        var root = Directory(string.Empty, path);
        AddHostChildren(root, new DirectoryInfo(path), "", exclude);
        return root;
    }

    private static void AddHostChildren(XisoBuildNode node, DirectoryInfo dir, string relative, Func<string, bool>? exclude)
    {
        foreach (var info in dir.EnumerateFileSystemInfos())
        {
            string rel = relative + "/" + info.Name;
            if (exclude?.Invoke(rel) == true)
            {
                continue;
            }
            if (info is DirectoryInfo sub)
            {
                var child = node.Add(Directory(info.Name, info.FullName));
                AddHostChildren(child, sub, rel, exclude);
            }
            else if (info is FileInfo file)
            {
                node.Add(File(info.Name, file.Length, ContentProvider.FromHostFile(file.FullName), file.FullName));
            }
        }
    }
}

/// <summary>Options for <see cref="XisoBuilder"/>.</summary>
public sealed record XisoBuildOptions
{
    /// <summary>Share one copy of byte-identical files (they point at the same sectors). Requires hashing every file.</summary>
    public bool Deduplicate { get; init; }

    /// <summary>Apply extract-xiso's media-check patch to every <c>.xbe</c>.</summary>
    public bool MediaPatch { get; init; } = true;

    /// <summary>Raw FILETIME written to the volume descriptor. Use 0 for deterministic output.</summary>
    public ulong FileTime { get; init; }

    /// <summary>Text after "in!xiso!" in the optimized tag (truncated/padded to 16 characters).</summary>
    public string TagVersion { get; init; } = "xbox-iso-library";
}

/// <summary>Statistics from a layout pass.</summary>
public sealed record XisoBuildResult(
    VirtualImage Image,
    int FileCount,
    int DirectoryCount,
    long FileBytes,
    int DuplicateFiles,
    long DuplicateBytesSaved,
    int PatchedExecutables);

/// <summary>
/// Lays out a new, minimal XISO (extract-xiso compatible): root table at sector 0x108, each directory's
/// files directly after its table, balanced binary-search-tree directory tables, 0xFF sector padding.
/// The result is a <see cref="VirtualImage"/>; nothing is written until it is copied to an output.
/// </summary>
public static class XisoBuilder
{
    public static XisoBuildResult Build(
        XisoBuildNode root,
        XisoBuildOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!root.IsDirectory)
        {
            throw new ArgumentException("Root must be a directory.", nameof(root));
        }

        Validate(root, "");

        int patched = 0;
        if (options.MediaPatch)
        {
            foreach (var f in root.AllFiles())
            {
                if (f.Size > 0 && Xbe.XbeMediaPatch.AppliesTo(f.Name))
                {
                    var data = ReadAll(f);
                    if (Xbe.XbeMediaPatch.Apply(data) > 0)
                    {
                        patched++;
                    }
                    f.Content = ContentProvider.FromBytes(data);
                }
            }
        }

        var files = root.AllFiles().ToList();
        if (options.Deduplicate)
        {
            HashFiles(files, progress, cancellationToken);
        }

        // Layout: identical algorithm to extract-xiso's calculate_directory_offsets.
        LayoutTable(root);
        uint current = XdvdfsConstants.RootDirectorySector;
        var dedupe = new Dictionary<(long, string), uint>();
        int duplicates = 0;
        long duplicateBytes = 0;
        Assign(root, ref current, dedupe, ref duplicates, ref duplicateBytes);

        long totalBytes = (long)current * XdvdfsConstants.SectorSize;
        totalBytes = (totalBytes + XdvdfsConstants.FileModulus - 1) / XdvdfsConstants.FileModulus * XdvdfsConstants.FileModulus;

        var builder = new VirtualImage.Builder();
        builder.Add(0, BuildHeader(root, totalBytes, options));
        int dirCount = 0;
        EmitSegments(root, builder, ref dirCount);

        return new XisoBuildResult(
            builder.Build(totalBytes),
            files.Count,
            dirCount,
            files.Sum(f => f.Size),
            duplicates,
            duplicateBytes,
            patched);
    }

    private static void Validate(XisoBuildNode dir, string path)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in dir.Children)
        {
            if (c.Name.Length == 0 || c.Name is "." or ".." || c.Name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
            {
                throw new InvalidDataException($"Invalid file name \"{c.Name}\" in {path}/.");
            }
            if (c.Name.Any(ch => ch > 0xFF))
            {
                throw new InvalidDataException($"File name \"{c.Name}\" in {path}/ has characters that cannot be stored on an Xbox disc.");
            }
            if (!names.Add(c.Name))
            {
                throw new InvalidDataException($"Duplicate name \"{c.Name}\" in {path}/ (names are case-insensitive).");
            }
            if (c.IsDirectory)
            {
                Validate(c, path + "/" + c.Name);
            }
        }
    }

    private static byte[] ReadAll(XisoBuildNode file)
    {
        var data = new byte[file.Size];
        int read = 0;
        while (read < data.Length)
        {
            int n = file.Content!.Read(read, data.AsSpan(read));
            if (n <= 0)
            {
                throw new EndOfStreamException($"{file.SourcePath ?? file.Name} ended early.");
            }
            read += n;
        }
        return data;
    }

    private static void HashFiles(List<XisoBuildNode> files, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        // Only files that share their size with another file can be duplicates; the rest keep no
        // ContentKey and are laid out normally, so there is no need to read them.
        files = files.GroupBy(f => f.Size).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        long total = files.Sum(f => f.Size);
        long done = 0;
        var buffer = new byte[1 << 20];
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (f.Size == 0)
            {
                continue;
            }
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (offset < f.Size)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, f.Size - offset);
                int n = f.Content!.Read(offset, buffer.AsSpan(0, want));
                if (n <= 0)
                {
                    throw new EndOfStreamException($"{f.SourcePath ?? f.Name} ended early.");
                }
                sha.AppendData(buffer, 0, n);
                offset += n;
                done += n;
                progress?.Report(new OperationProgress(OperationStage.Deduplicating, done, total, f.SourcePath ?? f.Name));
            }
            f.ContentKey = (f.Size, sha.GetHashAndReset());
        }
    }

    private static int EntryLength(string name)
    {
        int len = XdvdfsConstants.EntryHeaderSize + name.Length;
        return (len + 3) & ~3;
    }

    private static XisoBuildNode? Balanced(List<XisoBuildNode> sorted, int lo, int hi)
    {
        if (lo > hi)
        {
            return null;
        }
        int mid = (lo + hi + 1) / 2;
        var n = sorted[mid];
        n.Left = Balanced(sorted, lo, mid - 1);
        n.Right = Balanced(sorted, mid + 1, hi);
        return n;
    }

    private static IEnumerable<XisoBuildNode> Prefix(XisoBuildNode? node)
    {
        var stack = new Stack<XisoBuildNode>();
        if (node is not null)
        {
            stack.Push(node);
        }
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            if (n.Right is not null)
            {
                stack.Push(n.Right);
            }
            if (n.Left is not null)
            {
                stack.Push(n.Left);
            }
        }
    }

    private static void LayoutTable(XisoBuildNode dir)
    {
        var sorted = dir.Children.OrderBy(c => c.Name, XdvdfsNameComparer.Instance).ToList();
        dir.TreeRoot = Balanced(sorted, 0, sorted.Count - 1);
        int size = 0;
        foreach (var n in Prefix(dir.TreeRoot))
        {
            int len = EntryLength(n.Name);
            // Entries never straddle a sector boundary.
            if (size / XdvdfsConstants.SectorSize != (size + len - 1) / XdvdfsConstants.SectorSize)
            {
                size = (size + XdvdfsConstants.SectorSize - 1) / XdvdfsConstants.SectorSize * XdvdfsConstants.SectorSize;
            }
            n.TableOffset = size;
            size += len;
        }
        if ((long)size / 4 > ushort.MaxValue)
        {
            throw new InvalidDataException("Directory has too many entries for an XDVDFS table.");
        }
        dir.TableSize = size;
        dir.TableAllocation = sorted.Count == 0
            ? XdvdfsConstants.SectorSize
            : (uint)((size + XdvdfsConstants.SectorSize - 1) / XdvdfsConstants.SectorSize * XdvdfsConstants.SectorSize);
    }

    private static void Assign(XisoBuildNode dir, ref uint current, Dictionary<(long, string), uint> dedupe, ref int duplicates, ref long duplicateBytes)
    {
        dir.Sector = current;
        current = checked(current + dir.TableAllocation / XdvdfsConstants.SectorSize);
        if (dir.Children.Count == 0)
        {
            return;
        }

        foreach (var n in Prefix(dir.TreeRoot))
        {
            if (n.IsDirectory)
            {
                continue;
            }
            n.IsDuplicate = false;
            if (n.ContentKey is { } key && n.Size > 0)
            {
                var k = (key.Size, Convert.ToHexString(key.Hash));
                if (dedupe.TryGetValue(k, out uint existing))
                {
                    n.Sector = existing;
                    n.IsDuplicate = true;
                    duplicates++;
                    duplicateBytes += n.Size;
                    continue;
                }
                dedupe[k] = current;
            }
            n.Sector = current;
            current = checked(current + (uint)XdvdfsConstants.SectorsFor(n.Size));
        }

        foreach (var n in Prefix(dir.TreeRoot))
        {
            if (n.IsDirectory)
            {
                LayoutTable(n);
                Assign(n, ref current, dedupe, ref duplicates, ref duplicateBytes);
            }
        }
    }

    private static byte[] BuildTable(XisoBuildNode dir)
    {
        var table = new byte[dir.TableAllocation];
        table.AsSpan().Fill(XdvdfsConstants.PadByte);
        foreach (var n in Prefix(dir.TreeRoot))
        {
            var e = table.AsSpan(n.TableOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(e, (ushort)(n.Left is null ? 0 : n.Left.TableOffset / 4));
            BinaryPrimitives.WriteUInt16LittleEndian(e[2..], (ushort)(n.Right is null ? 0 : n.Right.TableOffset / 4));
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], n.Sector);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], n.IsDirectory ? n.TableAllocation : (uint)n.Size);
            e[12] = n.IsDirectory ? XdvdfsConstants.AttributeDirectory : XdvdfsConstants.AttributeArchive;
            e[13] = (byte)n.Name.Length;
            Encoding.Latin1.GetBytes(n.Name, e.Slice(XdvdfsConstants.EntryHeaderSize, n.Name.Length));
        }
        return table;
    }

    private static void EmitSegments(XisoBuildNode dir, VirtualImage.Builder builder, ref int dirCount)
    {
        dirCount++;
        builder.Add((long)dir.Sector * XdvdfsConstants.SectorSize, BuildTable(dir));
        foreach (var c in dir.Children)
        {
            if (c.IsDirectory)
            {
                EmitSegments(c, builder, ref dirCount);
            }
            else if (!c.IsDuplicate && c.Size > 0)
            {
                long start = (long)c.Sector * XdvdfsConstants.SectorSize;
                builder.Add(start, c.Size, c.Content!);
                long pad = XdvdfsConstants.SectorsFor(c.Size) * XdvdfsConstants.SectorSize - c.Size;
                builder.Fill(start + c.Size, pad, XdvdfsConstants.PadByte);
            }
        }
    }

    private static byte[] BuildHeader(XisoBuildNode root, long totalBytes, XisoBuildOptions options)
    {
        var h = new byte[XdvdfsConstants.RootDirectorySector * XdvdfsConstants.SectorSize];
        uint totalSectors = (uint)(totalBytes / XdvdfsConstants.SectorSize);

        // ECMA-119 stub at sector 16, as written by extract-xiso (helps some tools recognise the file).
        const int ecma = 0x8000;
        "\u0001CD001\u0001"u8.CopyTo(h.AsSpan(ecma));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(ecma + 80), totalSectors);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(ecma + 84), totalSectors);
        new byte[] { 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x08, 0x08, 0x00 }.CopyTo(h.AsSpan(ecma + 120));
        h.AsSpan(ecma + 190, 813 - 190).Fill((byte)' ');
        for (int i = 0; i < 4; i++)
        {
            "0000000000000000"u8.CopyTo(h.AsSpan(ecma + 813 + i * 16));
        }
        h[ecma + 813 + 64] = 0x01;
        // Volume descriptor set terminator (type 0xFF).
        new byte[] { 0xFF, (byte)'C', (byte)'D', (byte)'0', (byte)'0', (byte)'1', 0x01 }.CopyTo(h.AsSpan(ecma + XdvdfsConstants.SectorSize));

        // Optimized tag.
        string version = (options.TagVersion ?? string.Empty).PadRight(16)[..16];
        Encoding.ASCII.GetBytes("in!xiso!" + version).CopyTo(h.AsSpan(XdvdfsConstants.OptimizedTagOffset));

        // Volume descriptor.
        var d = h.AsSpan(XdvdfsConstants.DescriptorOffset, XdvdfsConstants.SectorSize);
        XdvdfsConstants.Magic.CopyTo(d);
        BinaryPrimitives.WriteUInt32LittleEndian(d[20..], root.Sector);
        BinaryPrimitives.WriteUInt32LittleEndian(d[24..], root.TableAllocation);
        BinaryPrimitives.WriteUInt64LittleEndian(d[28..], options.FileTime);
        XdvdfsConstants.Magic.CopyTo(d[0x7EC..]);
        return h;
    }
}
