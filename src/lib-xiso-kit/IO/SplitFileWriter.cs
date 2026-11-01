using Microsoft.Win32.SafeHandles;

namespace LibXisoKit.IO;

/// <summary>
/// Random-access writer that spreads one logical file over numbered parts (<c>name.1.ext</c>, <c>name.2.ext</c>…)
/// of at most <c>partSize</c> bytes. When everything fits in one part it is renamed to the plain output name.
/// Call <see cref="Complete"/> on success; disposing without completing deletes everything written.
/// </summary>
internal sealed class SplitFileWriter : IDisposable
{
    private readonly string _outputPath;
    private readonly long? _partSize;
    private readonly List<SafeFileHandle> _handles = new();
    private readonly List<string> _paths = new();
    private bool _completed;

    public SplitFileWriter(string outputPath, long? partSize, bool overwrite)
    {
        if (partSize is <= 0 || (partSize is { } p && p % 2048 != 0))
        {
            throw new ArgumentException("Split size must be a positive multiple of 2048.", nameof(partSize));
        }
        _outputPath = Path.GetFullPath(outputPath);
        _partSize = partSize;
        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        PrepareTargets(_outputPath, partSize is not null, overwrite);
    }

    /// <summary>Checks for (and optionally deletes) an existing output and its numbered parts.</summary>
    internal static void PrepareTargets(string outputPath, bool split, bool overwrite)
    {
        var existing = new List<string>();
        if (File.Exists(outputPath))
        {
            existing.Add(outputPath);
        }
        for (int i = 1; File.Exists(SplitParts.PartPath(outputPath, i)); i++)
        {
            existing.Add(SplitParts.PartPath(outputPath, i));
        }
        if (existing.Count == 0)
        {
            return;
        }
        if (!overwrite)
        {
            throw new IOException($"Output already exists: {existing[0]}");
        }
        foreach (var f in existing)
        {
            File.Delete(f);
        }
    }

    public long BytesWritten { get; private set; }

    public void Write(long position, ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int part;
            long inPart;
            int count;
            if (_partSize is { } size)
            {
                part = (int)(position / size);
                inPart = position % size;
                count = (int)Math.Min(data.Length, size - inPart);
            }
            else
            {
                part = 0;
                inPart = position;
                count = data.Length;
            }

            var handle = GetHandle(part);
            RandomAccess.Write(handle, data[..count], inPart);
            data = data[count..];
            position += count;
            BytesWritten = Math.Max(BytesWritten, position);
        }
    }

    private SafeFileHandle GetHandle(int part)
    {
        while (_handles.Count <= part)
        {
            int index = _handles.Count;
            string path = _partSize is null ? _outputPath : SplitParts.PartPath(_outputPath, index + 1);
            _handles.Add(File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
            _paths.Add(path);
        }
        return _handles[part];
    }

    /// <summary>Finalises the parts and returns their paths.</summary>
    public IReadOnlyList<string> Complete()
    {
        if (_handles.Count == 0)
        {
            GetHandle(0); // empty output still produces a file
        }
        foreach (var h in _handles)
        {
            h.Dispose();
        }
        _completed = true;

        if (_partSize is not null && _paths.Count == 1)
        {
            File.Move(_paths[0], _outputPath);
            _paths[0] = _outputPath;
        }
        return _paths.ToArray();
    }

    public void Dispose()
    {
        if (_completed)
        {
            return;
        }
        foreach (var h in _handles)
        {
            h.Dispose();
        }
        foreach (var p in _paths)
        {
            try
            {
                File.Delete(p);
            }
            catch (IOException)
            {
                // Best effort cleanup.
            }
        }
    }
}
