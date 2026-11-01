using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace XboxIsoLibrary.IO;

/// <summary>Container format of an image file on disk.</summary>
public enum ContainerFormat
{
    /// <summary>Plain sector image (Redump ISO, XISO), possibly split into numbered parts.</summary>
    Iso,
    /// <summary>Cerbios Compressed Image (LZ4, per-part headers).</summary>
    Cci,
    /// <summary>CISO v2 / Project Stellar CSO (LZ4, byte-split parts).</summary>
    Cso,
}

/// <summary>
/// Random-access, read-only view of a logical (uncompressed) disc image.
/// Implementations are thread-safe: concurrent <see cref="Read"/> calls are allowed.
/// </summary>
public abstract class ImageSource : IDisposable
{
    /// <summary>Logical (uncompressed) length in bytes.</summary>
    public abstract long Length { get; }

    /// <summary>Format of the container on disk.</summary>
    public abstract ContainerFormat Format { get; }

    /// <summary>Files on disk backing this source, in order.</summary>
    public IReadOnlyList<string> Files { get; protected init; } = Array.Empty<string>();

    /// <summary>
    /// Reads up to <paramref name="buffer"/>.Length bytes at <paramref name="position"/>.
    /// Returns fewer bytes only when the end of the image is reached.
    /// </summary>
    public abstract int Read(long position, Span<byte> buffer);

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes or throws.</summary>
    public void ReadExactly(long position, Span<byte> buffer)
    {
        int n = Read(position, buffer);
        if (n != buffer.Length)
        {
            throw new EndOfStreamException(
                $"Unexpected end of image at offset 0x{position + n:X} (wanted {buffer.Length} bytes, got {n}).");
        }
    }

    /// <summary>Wraps this source as a read-only seekable <see cref="Stream"/>. The source is not disposed with the stream.</summary>
    public Stream AsStream() => new ImageSourceStream(this);

    public virtual void Dispose() => GC.SuppressFinalize(this);
}

/// <summary>Resolves numbered split parts such as <c>game.1.iso</c>, <c>game.2.iso</c>.</summary>
public static class SplitParts
{
    private static readonly Regex PartPattern = new(@"^(?<base>.+)\.(?<n>\d+)\.(?<ext>[^.]+)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns every part of a split set when <paramref name="path"/> names any part of it;
    /// otherwise returns just <paramref name="path"/>.
    /// </summary>
    public static IReadOnlyList<string> Resolve(string path)
    {
        string full = Path.GetFullPath(path);
        string name = Path.GetFileName(full);
        string dir = Path.GetDirectoryName(full) ?? ".";
        var m = PartPattern.Match(name);
        if (!m.Success)
        {
            return new[] { full };
        }

        string baseName = m.Groups["base"].Value;
        string ext = m.Groups["ext"].Value;
        string first = Path.Combine(dir, $"{baseName}.1.{ext}");
        if (!File.Exists(first))
        {
            return new[] { full };
        }

        var parts = new List<string>();
        for (int i = 1; ; i++)
        {
            string p = Path.Combine(dir, $"{baseName}.{i}.{ext}");
            if (!File.Exists(p))
            {
                break;
            }
            parts.Add(p);
        }
        return parts;
    }

    /// <summary>Builds the path of part <paramref name="index"/> (1-based) for an output path like <c>out/game.iso</c>.</summary>
    public static string PartPath(string outputPath, int index)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(outputPath);
        string ext = Path.GetExtension(outputPath);
        return Path.Combine(dir, $"{stem}.{index}{ext}");
    }
}

/// <summary>A set of files read as one contiguous byte range (handles split ISO parts).</summary>
public sealed class ConcatenatedFileSource : ImageSource
{
    private readonly SafeFileHandle[] _handles;
    private readonly long[] _starts;
    private readonly long[] _lengths;
    private readonly long _length;
    private readonly ContainerFormat _format;

    public ConcatenatedFileSource(IReadOnlyList<string> files, ContainerFormat format = ContainerFormat.Iso)
    {
        if (files.Count == 0)
        {
            throw new ArgumentException("At least one file is required.", nameof(files));
        }

        _format = format;
        _handles = new SafeFileHandle[files.Count];
        _starts = new long[files.Count];
        _lengths = new long[files.Count];
        long pos = 0;
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                _handles[i] = File.OpenHandle(files[i], FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
                _starts[i] = pos;
                _lengths[i] = RandomAccess.GetLength(_handles[i]);
                pos += _lengths[i];
            }
        }
        catch
        {
            foreach (var h in _handles)
            {
                h?.Dispose();
            }
            throw;
        }
        _length = pos;
        Files = files.ToArray();
    }

    public override long Length => _length;

    public override ContainerFormat Format => _format;

    public override int Read(long position, Span<byte> buffer)
    {
        if (position < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        int total = 0;
        while (buffer.Length > 0 && position < _length)
        {
            int idx = FindPart(position);
            long inPart = position - _starts[idx];
            int want = (int)Math.Min(buffer.Length, _lengths[idx] - inPart);
            int n = RandomAccess.Read(_handles[idx], buffer[..want], inPart);
            if (n <= 0)
            {
                break;
            }
            total += n;
            position += n;
            buffer = buffer[n..];
        }
        return total;
    }

    private int FindPart(long position)
    {
        int idx = Array.BinarySearch(_starts, position);
        if (idx < 0)
        {
            idx = ~idx - 1;
        }
        // Skip zero-length parts.
        while (idx < _starts.Length - 1 && position >= _starts[idx] + _lengths[idx])
        {
            idx++;
        }
        return idx;
    }

    public override void Dispose()
    {
        foreach (var h in _handles)
        {
            h.Dispose();
        }
        base.Dispose();
    }
}

/// <summary>A window into another source, e.g. the game partition of a Redump image.</summary>
public sealed class SubrangeSource : ImageSource
{
    private readonly ImageSource _inner;
    private readonly long _offset;
    private readonly long _length;

    public SubrangeSource(ImageSource inner, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > inner.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        _inner = inner;
        _offset = offset;
        _length = Math.Min(length, inner.Length - offset);
        Files = inner.Files;
    }

    public override long Length => _length;

    public override ContainerFormat Format => _inner.Format;

    public override int Read(long position, Span<byte> buffer)
    {
        if (position >= _length)
        {
            return 0;
        }
        int want = (int)Math.Min(buffer.Length, _length - position);
        return _inner.Read(_offset + position, buffer[..want]);
    }
}

internal sealed class ImageSourceStream : Stream
{
    private readonly ImageSource _source;
    private long _position;

    public ImageSourceStream(ImageSource source) => _source = source;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _source.Length;

    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = _source.Read(_position, buffer);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
