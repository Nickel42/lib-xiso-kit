using System.Buffers;
using System.Buffers.Binary;
using NoIntrovert.XboxIso.IO;
using Microsoft.Win32.SafeHandles;

namespace NoIntrovert.XboxIso.Compression;

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

    public override int Read(long position, Span<byte> buffer)
    {
        int total = 0;
        byte[] sectorBuf = ArrayPool<byte>.Shared.Rent(CompressedFormats.BlockSize);
        byte[] blockBuf = ArrayPool<byte>.Shared.Rent(CompressedFormats.BlockSize * 2);
        try
        {
            while (buffer.Length > 0 && position < _length)
            {
                long sector = position / CompressedFormats.BlockSize;
                int within = (int)(position % CompressedFormats.BlockSize);
                ReadSector(sector, sectorBuf.AsSpan(0, CompressedFormats.BlockSize), blockBuf);
                int n = Math.Min(buffer.Length, CompressedFormats.BlockSize - within);
                sectorBuf.AsSpan(within, n).CopyTo(buffer);
                buffer = buffer[n..];
                position += n;
                total += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sectorBuf);
            ArrayPool<byte>.Shared.Return(blockBuf);
        }
        return total;
    }

    private void ReadSector(long sector, Span<byte> output, byte[] blockBuf)
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
        uint entry = part.Index[local];
        long start = (long)(entry & 0x7FFFFFFF) << part.Alignment;
        long end = (long)(part.Index[local + 1] & 0x7FFFFFFF) << part.Alignment;
        int size = checked((int)(end - start));
        bool compressed = (entry & 0x80000000) != 0;

        if (!compressed && size == CompressedFormats.BlockSize)
        {
            if (RandomAccess.Read(part.Handle, output, start) != output.Length)
            {
                throw new EndOfStreamException("Truncated CCI block.");
            }
            return;
        }

        if (size <= 0 || size > blockBuf.Length)
        {
            throw new InvalidDataException("Corrupt CCI index entry.");
        }
        var block = blockBuf.AsSpan(0, size);
        if (RandomAccess.Read(part.Handle, block, start) != size)
        {
            throw new EndOfStreamException("Truncated CCI block.");
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

    public override int Read(long position, Span<byte> buffer)
    {
        int total = 0;
        byte[] sectorBuf = ArrayPool<byte>.Shared.Rent(_blockSize);
        byte[] blockBuf = ArrayPool<byte>.Shared.Rent(_blockSize * 2);
        try
        {
            while (buffer.Length > 0 && position < _length)
            {
                long sector = position / _blockSize;
                int within = (int)(position % _blockSize);
                ReadSector(sector, sectorBuf.AsSpan(0, _blockSize), blockBuf);
                int n = Math.Min(buffer.Length, _blockSize - within);
                sectorBuf.AsSpan(within, n).CopyTo(buffer);
                buffer = buffer[n..];
                position += n;
                total += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sectorBuf);
            ArrayPool<byte>.Shared.Return(blockBuf);
        }
        return total;
    }

    private void ReadSector(long sector, Span<byte> output, byte[] blockBuf)
    {
        uint entry = _index[sector];
        long start = (long)(entry & 0x7FFFFFFF) << _alignment;
        bool compressed = (entry & 0x80000000) != 0;
        if (!compressed)
        {
            _raw.ReadExactly(start, output);
            return;
        }

        long end = (long)(_index[sector + 1] & 0x7FFFFFFF) << _alignment;
        int size = checked((int)(end - start));
        if (size < 4 || size > blockBuf.Length)
        {
            throw new InvalidDataException("Corrupt CSO index entry.");
        }
        var block = blockBuf.AsSpan(0, size);
        _raw.ReadExactly(start, block);
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
