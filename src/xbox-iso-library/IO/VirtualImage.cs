namespace XboxIsoLibrary.IO;

/// <summary>Supplies bytes for a segment of a <see cref="VirtualImage"/>.</summary>
public abstract class ContentProvider
{
    /// <summary>Reads from <paramref name="offset"/> into <paramref name="buffer"/>; returns bytes read.</summary>
    public abstract int Read(long offset, Span<byte> buffer);

    public static ContentProvider FromBytes(byte[] data) => new BytesContent(data);

    public static ContentProvider FromSource(ImageSource source, long offset) => new SourceContent(source, offset);

    public static ContentProvider FromHostFile(string path) => new HostFileContent(path);

    private sealed class BytesContent : ContentProvider
    {
        private readonly byte[] _data;
        public BytesContent(byte[] data) => _data = data;

        public override int Read(long offset, Span<byte> buffer)
        {
            if (offset >= _data.Length)
            {
                return 0;
            }
            int n = (int)Math.Min(buffer.Length, _data.Length - offset);
            _data.AsSpan((int)offset, n).CopyTo(buffer);
            return n;
        }
    }

    private sealed class SourceContent : ContentProvider
    {
        private readonly ImageSource _source;
        private readonly long _offset;

        public SourceContent(ImageSource source, long offset)
        {
            _source = source;
            _offset = offset;
        }

        public override int Read(long offset, Span<byte> buffer) => _source.Read(_offset + offset, buffer);
    }

    private sealed class HostFileContent : ContentProvider
    {
        private readonly string _path;
        public HostFileContent(string path) => _path = path;

        public override int Read(long offset, Span<byte> buffer)
        {
            using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int total = 0;
            while (buffer.Length > 0)
            {
                int n = RandomAccess.Read(handle, buffer, offset);
                if (n <= 0)
                {
                    break;
                }
                total += n;
                offset += n;
                buffer = buffer[n..];
            }
            return total;
        }
    }
}

/// <summary>
/// An image assembled on the fly from segments (header bytes, directory tables, file data, padding).
/// Nothing is materialised on disk until it is written; reads outside any segment return zeros.
/// </summary>
public sealed class VirtualImage : ImageSource
{
    private readonly long[] _starts;
    private readonly Segment[] _segments;
    private readonly long _length;

    private readonly record struct Segment(long Start, long Length, ContentProvider? Content, byte Fill);

    private VirtualImage(List<Segment> segments, long length)
    {
        segments.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 1; i < segments.Count; i++)
        {
            if (segments[i].Start < segments[i - 1].Start + segments[i - 1].Length)
            {
                throw new InvalidOperationException(
                    $"Internal layout error: segments overlap at 0x{segments[i].Start:X}.");
            }
        }
        _segments = segments.ToArray();
        _starts = _segments.Select(s => s.Start).ToArray();
        _length = length;
    }

    public override long Length => _length;

    public override ContainerFormat Format => ContainerFormat.Iso;

    public override int Read(long position, Span<byte> buffer)
    {
        if (position >= _length)
        {
            return 0;
        }
        int total = (int)Math.Min(buffer.Length, _length - position);
        buffer = buffer[..total];

        int idx = Array.BinarySearch(_starts, position);
        if (idx < 0)
        {
            idx = ~idx - 1;
        }

        while (buffer.Length > 0)
        {
            // Skip segments that end before the current position.
            while (idx >= 0 && idx < _segments.Length && _segments[idx].Start + _segments[idx].Length <= position)
            {
                idx++;
            }
            if (idx < 0)
            {
                idx = 0;
            }

            long nextStart = idx < _segments.Length ? _segments[idx].Start : long.MaxValue;
            if (idx >= _segments.Length || position < nextStart)
            {
                // Gap: zeros until the next segment.
                int gap = (int)Math.Min(buffer.Length, nextStart - position);
                buffer[..gap].Clear();
                buffer = buffer[gap..];
                position += gap;
                continue;
            }

            var seg = _segments[idx];
            long within = position - seg.Start;
            int n = (int)Math.Min(buffer.Length, seg.Length - within);
            var slice = buffer[..n];
            if (seg.Content is null)
            {
                slice.Fill(seg.Fill);
            }
            else
            {
                int read = 0;
                while (read < n)
                {
                    int r = seg.Content.Read(within + read, slice[read..]);
                    if (r <= 0)
                    {
                        throw new EndOfStreamException($"Source data ended early while building segment at 0x{seg.Start:X}.");
                    }
                    read += r;
                }
            }
            buffer = buffer[n..];
            position += n;
            idx++;
        }
        return total;
    }

    /// <summary>Collects segments and produces a <see cref="VirtualImage"/>.</summary>
    public sealed class Builder
    {
        private readonly List<Segment> _segments = new();

        public Builder Add(long start, long length, ContentProvider content)
        {
            if (length > 0)
            {
                _segments.Add(new Segment(start, length, content, 0));
            }
            return this;
        }

        public Builder Add(long start, byte[] data) => Add(start, data.LongLength, ContentProvider.FromBytes(data));

        public Builder Fill(long start, long length, byte value)
        {
            if (length > 0 && value != 0)
            {
                _segments.Add(new Segment(start, length, null, value));
            }
            return this;
        }

        public VirtualImage Build(long length) => new(_segments, length);
    }
}
