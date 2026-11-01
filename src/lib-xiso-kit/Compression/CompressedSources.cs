using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using LibXisoKit.IO;
using Microsoft.Win32.SafeHandles;

namespace LibXisoKit.Compression;

/// <summary>Constants shared by the CCI and CSO containers.</summary>
public static class CompressedFormats
{
    public const uint CciMagic = 0x4D494343; // "CCIM"
    public const uint CsoMagic = 0x4F534943; // "CISO"
    public const int BlockSize = 2048;
    public const int IndexAlignment = 2;

    /// <summary>Default split size for byte-split CSO parts (sector aligned, below the FATX 4 GiB limit).</summary>
    public const long CsoSplitPoint = 0xFFBF6000;
}

/// <summary>
/// Shared read path for CCI and CSO. Stored blocks are written back to back, so a run of sectors is
/// fetched with one read and then decoded in parallel, instead of one read and one decode per 2 KiB sector.
/// </summary>
internal static class SectorBatch
{
    /// <summary>Most sectors fetched per batch (4 MiB of output).</summary>
    public const int MaxSectors = 2048;

    /// <summary>Smaller batches are decoded on the calling thread.</summary>
    private const int ParallelThreshold = 64;
    private const int SectorsPerTask = 64;

    public delegate void BlockDecoder(ReadOnlySpan<byte> stored, bool compressed, Span<byte> output);

    /// <summary>File offset of the block that <paramref name="entry"/> points at.</summary>
    public static long Offset(uint entry, int alignment) => (long)(entry & 0x7FFFFFFF) << alignment;

    /// <summary>
    /// Decodes <paramref name="count"/> sectors starting at index entry <paramref name="first"/>.
    /// <paramref name="stored"/> holds the file bytes from <paramref name="baseOffset"/> to the end of the last block.
    /// </summary>
    public static void Decode(byte[] stored, int storedLength, uint[] index, long first, int count, int alignment, long baseOffset, byte[] output, BlockDecoder decoder)
    {
        void DecodeRange(int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                uint entry = index[first + i];
                long start = Offset(entry, alignment) - baseOffset;
                long end = Offset(index[first + i + 1], alignment) - baseOffset;
                if (start < 0 || end < start || end > storedLength)
                {
                    throw new InvalidDataException("Corrupt compressed image index entry.");
                }
                decoder(stored.AsSpan((int)start, (int)(end - start)), (entry & 0x80000000) != 0,
                    output.AsSpan(i * CompressedFormats.BlockSize, CompressedFormats.BlockSize));
            }
        }

        if (count < ParallelThreshold)
        {
            DecodeRange(0, count);
            return;
        }
        try
        {
            int tasks = (count + SectorsPerTask - 1) / SectorsPerTask;
            Parallel.For(0, tasks, t => DecodeRange(t * SectorsPerTask, Math.Min(count, (t + 1) * SectorsPerTask)));
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Throw(ae.InnerExceptions[0]); // keep the same exception types as the serial path
        }
    }

    /// <summary>
    /// Serves a logical read by fetching whole sectors in batches through <paramref name="readSectors"/>,
    /// which may return fewer sectors than asked (e.g. at a part boundary) but at least one.
    /// </summary>
    public static int Read(long position, Span<byte> buffer, long length, Func<long, int, byte[], int> readSectors)
    {
        const int bs = CompressedFormats.BlockSize;
        int total = 0;
        long sectorCount = length / bs;
        int capacity = (int)Math.Min(MaxSectors, ((long)buffer.Length + bs - 1) / bs + 1);
        byte[] decoded = ArrayPool<byte>.Shared.Rent(capacity * bs);
        try
        {
            while (buffer.Length > 0 && position < length)
            {
                long sector = position / bs;
                int within = (int)(position % bs);
                int want = (int)Math.Min(Math.Min(((long)within + buffer.Length + bs - 1) / bs, capacity), sectorCount - sector);
                int got = readSectors(sector, want, decoded);
                int n = (int)Math.Min(buffer.Length, (long)got * bs - within);
                decoded.AsSpan(within, n).CopyTo(buffer);
                buffer = buffer[n..];
                position += n;
                total += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(decoded);
        }
        return total;
    }
}

/// <summary>Reads Cerbios Compressed Images (<c>.cci</c>), including split sets where each part has its own header.</summary>
public sealed class CciSource : ImageSource
{
    private sealed class Part
    {
        public required SafeFileHandle Handle;
        public required long FirstSector;
        public required long SectorCount;
        public required uint[] Index;
        public required int Alignment;
    }

    private readonly Part[] _parts;
    private readonly long _length;

    public CciSource(IReadOnlyList<string> files)
    {
        var parts = new List<Part>();
        long sector = 0;
        try
        {
            Span<byte> header = stackalloc byte[32];
            foreach (var file in files)
            {
                var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
                try
                {
                    if (RandomAccess.Read(handle, header, 0) != 32)
                    {
                        throw new InvalidDataException($"{file}: truncated CCI header.");
                    }
                    uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
                    uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
                    ulong uncompressed = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
                    ulong indexOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
                    uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
                    byte version = header[28];
                    byte alignment = header[29];
                    if (magic != CompressedFormats.CciMagic || headerSize != 32 || blockSize != CompressedFormats.BlockSize || version != 1 || alignment > 16)
                    {
                        throw new InvalidDataException($"{file}: not a supported CCI file.");
                    }

                    long count = (long)(uncompressed / blockSize);
                    var raw = new byte[checked((int)((count + 1) * 4))];
                    if (RandomAccess.Read(handle, raw, (long)indexOffset) != raw.Length)
                    {
                        throw new InvalidDataException($"{file}: truncated CCI index.");
                    }
                    var index = new uint[count + 1];
                    for (int i = 0; i < index.Length; i++)
                    {
                        index[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4));
                    }

                    parts.Add(new Part { Handle = handle, FirstSector = sector, SectorCount = count, Index = index, Alignment = alignment });
                    sector += count;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
        }
        catch
        {
            foreach (var p in parts)
            {
                p.Handle.Dispose();
            }
            throw;
        }

        _parts = parts.ToArray();
        _length = sector * CompressedFormats.BlockSize;
        Files = files.ToArray();
    }

    public override long Length => _length;

    public override ContainerFormat Format => ContainerFormat.Cci;

    public override int Read(long position, Span<byte> buffer) => SectorBatch.Read(position, buffer, _length, ReadSectors);

    /// <summary>Decodes up to <paramref name="count"/> sectors from one part into <paramref name="output"/>.</summary>
    private int ReadSectors(long sector, int count, byte[] output)
    {
        Part? part = null;
        foreach (var p in _parts)
        {
            if (sector >= p.FirstSector && sector < p.FirstSector + p.SectorCount)
            {
                part = p;
                break;
            }
        }
        if (part is null)
        {
            throw new EndOfStreamException("CCI sector out of range.");
        }

        long local = sector - part.FirstSector;
        count = (int)Math.Min(count, part.SectorCount - local);
        long start = SectorBatch.Offset(part.Index[local], part.Alignment);
        long end = SectorBatch.Offset(part.Index[local + count], part.Alignment);
        long size = end - start;
        if (size <= 0 || size > (long)count * CompressedFormats.BlockSize * 2)
        {
            throw new InvalidDataException("Corrupt CCI index entry.");
        }

        byte[] stored = ArrayPool<byte>.Shared.Rent((int)size);
        try
        {
            for (int done = 0; done < size;)
            {
                int n = RandomAccess.Read(part.Handle, stored.AsSpan(done, (int)size - done), start + done);
                if (n <= 0)
                {
                    throw new EndOfStreamException("Truncated CCI block.");
                }
                done += n;
            }
            SectorBatch.Decode(stored, (int)size, part.Index, local, count, part.Alignment, start, output, DecodeBlock);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(stored);
        }
        return count;
    }

    private static void DecodeBlock(ReadOnlySpan<byte> block, bool compressed, Span<byte> output)
    {
        int size = block.Length;
        if (!compressed && size == CompressedFormats.BlockSize)
        {
            block.CopyTo(output);
            return;
        }

        if (size <= 0 || size > CompressedFormats.BlockSize * 2)
        {
            throw new InvalidDataException("Corrupt CCI index entry.");
        }
        int padding = block[0];
        int payload = size - 1 - padding;
        if (payload <= 0)
        {
            throw new InvalidDataException("Corrupt CCI block padding.");
        }
        int written = Lz4Block.Decompress(block.Slice(1, payload), output);
        if (written != output.Length)
        {
            throw new InvalidDataException("CCI block decompressed to the wrong size.");
        }
    }

    public override void Dispose()
    {
        foreach (var p in _parts)
        {
            p.Handle.Dispose();
        }
        base.Dispose();
    }
}

/// <summary>Reads CISO v2 / Project Stellar <c>.cso</c> images (LZ4), including byte-split <c>.1.cso</c>/<c>.2.cso</c> sets.</summary>
public sealed class CsoSource : ImageSource
{
    private readonly ConcatenatedFileSource _raw;
    private readonly uint[] _index;
    private readonly int _alignment;
    private readonly int _blockSize;
    private readonly long _length;

    public CsoSource(IReadOnlyList<string> files)
    {
        _raw = new ConcatenatedFileSource(files, ContainerFormat.Cso);
        try
        {
            Span<byte> header = stackalloc byte[24];
            _raw.ReadExactly(0, header);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            ulong uncompressed = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
            _blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            byte version = header[20];
            _alignment = header[21];
            if (magic != CompressedFormats.CsoMagic || version != 2 || headerSize != 24 || _blockSize != CompressedFormats.BlockSize || _alignment > 16)
            {
                throw new InvalidDataException("Not a supported CSO (CISO v2) file.");
            }

            long count = (long)(uncompressed / (ulong)_blockSize);
            var raw = new byte[checked((int)((count + 1) * 4))];
            _raw.ReadExactly(24, raw);
            _index = new uint[count + 1];
            for (int i = 0; i < _index.Length; i++)
            {
                _index[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4));
            }
            _length = count * _blockSize;
        }
        catch
        {
            _raw.Dispose();
            throw;
        }
        Files = _raw.Files;
    }

    public override long Length => _length;

    public override ContainerFormat Format => ContainerFormat.Cso;

    public override int Read(long position, Span<byte> buffer) => SectorBatch.Read(position, buffer, _length, ReadSectors);

    /// <summary>Decodes <paramref name="count"/> sectors into <paramref name="output"/>. Split parts are handled by the concatenated source.</summary>
    private int ReadSectors(long sector, int count, byte[] output)
    {
        long start = SectorBatch.Offset(_index[sector], _alignment);
        long end = SectorBatch.Offset(_index[sector + count], _alignment);
        long size = end - start;
        if (size <= 0 || size > (long)count * _blockSize * 2)
        {
            throw new InvalidDataException("Corrupt CSO index entry.");
        }

        byte[] stored = ArrayPool<byte>.Shared.Rent((int)size);
        try
        {
            _raw.ReadExactly(start, stored.AsSpan(0, (int)size));
            SectorBatch.Decode(stored, (int)size, _index, sector, count, _alignment, start, output, DecodeBlock);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(stored);
        }
        return count;
    }

    private static void DecodeBlock(ReadOnlySpan<byte> block, bool compressed, Span<byte> output)
    {
        int size = block.Length;
        if (!compressed)
        {
            if (size < output.Length)
            {
                throw new InvalidDataException("Corrupt CSO index entry.");
            }
            block[..output.Length].CopyTo(output);
            return;
        }

        if (size < 4 || size > CompressedFormats.BlockSize * 2)
        {
            throw new InvalidDataException("Corrupt CSO index entry.");
        }
        uint blockLen = BinaryPrimitives.ReadUInt32LittleEndian(block);
        bool storedRaw = (blockLen & 0x80000000) != 0; // LZ4 frame "uncompressed block" flag
        int len = (int)(blockLen & 0x7FFFFFFF);
        if (len > size - 4)
        {
            throw new InvalidDataException("Corrupt CSO block length.");
        }
        if (storedRaw)
        {
            if (len != output.Length)
            {
                throw new InvalidDataException("Corrupt CSO raw block.");
            }
            block.Slice(4, len).CopyTo(output);
            return;
        }
        int written = Lz4Block.Decompress(block.Slice(4, len), output);
        if (written != output.Length)
        {
            throw new InvalidDataException("CSO block decompressed to the wrong size.");
        }
    }

    public override void Dispose()
    {
        _raw.Dispose();
        base.Dispose();
    }
}
