using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using NoIntrovert.XboxIso.Compression;

namespace NoIntrovert.XboxIso.IO;

/// <summary>Output container for <see cref="ImageWriter"/>.</summary>
public enum OutputFormat
{
    /// <summary>Plain XISO (<c>.iso</c>).</summary>
    Iso,
    /// <summary>Cerbios Compressed Image (<c>.cci</c>), LZ4 per sector. Playable on Cerbios BIOS.</summary>
    Cci,
    /// <summary>CISO v2 (<c>.cso</c>), LZ4 per sector. Playable on Project Stellar; readable by xemu forks.</summary>
    Cso,
}

/// <summary>What was written.</summary>
public sealed record WriteResult(
    IReadOnlyList<string> Files,
    long LogicalLength,
    long BytesOnDisk,
    string LogicalSha1);

/// <summary>Writes an <see cref="ImageSource"/> to disk as ISO, CCI or CSO, optionally split into FATX-sized parts.</summary>
public static class ImageWriter
{
    private const int SectorSize = CompressedFormats.BlockSize;
    private const int ChunkSectors = 2048; // 4 MiB per batch

    /// <summary>Conventional file extension for a format.</summary>
    public static string ExtensionFor(OutputFormat format) => format switch
    {
        OutputFormat.Cci => ".cci",
        OutputFormat.Cso => ".cso",
        _ => ".iso",
    };

    public static WriteResult Write(
        ImageSource image,
        string outputPath,
        OutputFormat format,
        long? splitSize = null,
        bool overwrite = false,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (image.Length % SectorSize != 0)
        {
            throw new ArgumentException("Image length must be a multiple of 2048 bytes.", nameof(image));
        }
        var throttled = new ThrottledProgress(progress);
        return format switch
        {
            OutputFormat.Iso => WriteIso(image, outputPath, splitSize, overwrite, throttled, cancellationToken),
            OutputFormat.Cso => WriteCso(image, outputPath, splitSize, overwrite, throttled, cancellationToken),
            OutputFormat.Cci => WriteCci(image, outputPath, splitSize, overwrite, throttled, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    private static WriteResult WriteIso(ImageSource image, string outputPath, long? splitSize, bool overwrite, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        using var writer = new SplitFileWriter(outputPath, splitSize, overwrite);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSectors * SectorSize);
        try
        {
            long pos = 0;
            while (pos < image.Length)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(ChunkSectors * SectorSize, image.Length - pos);
                image.ReadExactly(pos, buffer.AsSpan(0, n));
                sha.AppendData(buffer, 0, n);
                writer.Write(pos, buffer.AsSpan(0, n));
                pos += n;
                progress.Report(new OperationProgress(OperationStage.Writing, pos, image.Length));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        long written = writer.BytesWritten;
        var files = writer.Complete();
        return new WriteResult(files, image.Length, written, Convert.ToHexString(sha.GetHashAndReset()));
    }

    /// <summary>Compressed form of one sector (or null when it should be stored raw).</summary>
    private delegate int Encoder(ReadOnlySpan<byte> sector, Span<byte> scratch, Span<byte> block);

    private static int EncodeCci(ReadOnlySpan<byte> sector, Span<byte> scratch, Span<byte> block)
    {
        const int multiple = 1 << CompressedFormats.IndexAlignment;
        int len = Lz4Block.Compress(sector, scratch);
        if (len <= 0 || len >= SectorSize - (4 + multiple))
        {
            return -1;
        }
        int padding = ((len + 1 + multiple - 1) / multiple * multiple) - (len + 1);
        block[0] = (byte)padding;
        scratch[..len].CopyTo(block[1..]);
        block.Slice(1 + len, padding).Clear();
        return 1 + len + padding;
    }

    private static int EncodeCso(ReadOnlySpan<byte> sector, Span<byte> scratch, Span<byte> block)
    {
        int len = Lz4Block.Compress(sector, scratch);
        if (len <= 0 || 4 + len + 12 >= SectorSize)
        {
            return -1;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(block, (uint)len);
        scratch[..len].CopyTo(block[4..]);
        return 4 + len;
    }

    /// <summary>Reads a chunk and compresses every sector in parallel. Returns per-sector block lengths (-1 = raw).</summary>
    private static int CompressChunk(ImageSource image, long pos, int sectors, byte[] raw, byte[] blocks, int[] lengths, Encoder encoder)
    {
        int bytes = sectors * SectorSize;
        image.ReadExactly(pos, raw.AsSpan(0, bytes));
        int maxBlock = SectorSize * 2;
        Parallel.For(0, sectors, () => new byte[Lz4Block.MaxCompressedLength(SectorSize)], (i, _, scratch) =>
        {
            lengths[i] = encoder(raw.AsSpan(i * SectorSize, SectorSize), scratch, blocks.AsSpan(i * maxBlock, maxBlock));
            return scratch;
        }, _ => { });
        return bytes;
    }

    private static WriteResult WriteCso(ImageSource image, string outputPath, long? splitSize, bool overwrite, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        long sectorCount = image.Length / SectorSize;
        var index = new uint[sectorCount + 1];
        long indexBytes = index.LongLength * 4;
        long position = 24 + indexBytes;
        const int align = 1 << CompressedFormats.IndexAlignment;

        // CSO parts are a plain byte split at a fixed point; the header lives in part 1.
        long? partSize = splitSize is null ? null : CompressedFormats.CsoSplitPoint;
        if (splitSize is { } s && s < partSize)
        {
            partSize = s;
        }

        using var writer = new SplitFileWriter(outputPath, partSize, overwrite);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] raw = ArrayPool<byte>.Shared.Rent(ChunkSectors * SectorSize);
        byte[] blocks = ArrayPool<byte>.Shared.Rent(ChunkSectors * SectorSize * 2);
        int[] lengths = new int[ChunkSectors];
        Span<byte> zeros = stackalloc byte[align];
        zeros.Clear();
        try
        {
            long sector = 0;
            while (sector < sectorCount)
            {
                ct.ThrowIfCancellationRequested();
                int count = (int)Math.Min(ChunkSectors, sectorCount - sector);
                int bytes = CompressChunk(image, sector * SectorSize, count, raw, blocks, lengths, EncodeCso);
                sha.AppendData(raw, 0, bytes);
                for (int i = 0; i < count; i++)
                {
                    int misalign = (int)(position & (align - 1));
                    if (misalign != 0)
                    {
                        writer.Write(position, zeros[..(align - misalign)]);
                        position += align - misalign;
                    }
                    bool compressed = lengths[i] > 0;
                    index[sector + i] = (uint)(position >> CompressedFormats.IndexAlignment) | (compressed ? 0x80000000u : 0u);
                    var data = compressed
                        ? blocks.AsSpan(i * SectorSize * 2, lengths[i])
                        : raw.AsSpan(i * SectorSize, SectorSize);
                    writer.Write(position, data);
                    position += data.Length;
                }
                sector += count;
                progress.Report(new OperationProgress(OperationStage.Compressing, sector * SectorSize, image.Length));
            }
            // Align the end so the final index entry (end of the last block) is exact.
            int tailMisalign = (int)(position & (align - 1));
            if (tailMisalign != 0)
            {
                writer.Write(position, zeros[..(align - tailMisalign)]);
                position += align - tailMisalign;
            }
            CheckIndexRange(position);
            index[sectorCount] =(uint)(position >> CompressedFormats.IndexAlignment);

            var header = new byte[24 + indexBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(header, CompressedFormats.CsoMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 24);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (ulong)image.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), SectorSize);
            header[20] = 2;
            header[21] = CompressedFormats.IndexAlignment;
            for (long i = 0; i < index.LongLength; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan((int)(24 + i * 4)), index[i]);
            }
            writer.Write(0, header);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
            ArrayPool<byte>.Shared.Return(blocks);
        }
        long written = writer.BytesWritten;
        var files = writer.Complete();
        return new WriteResult(files, image.Length, written, Convert.ToHexString(sha.GetHashAndReset()));
    }

    private static WriteResult WriteCci(ImageSource image, string outputPath, long? splitSize, bool overwrite, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        long sectorCount = image.Length / SectorSize;
        long limit = splitSize ?? long.MaxValue;
        string fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        SplitFileWriter.PrepareTargets(fullOutput, splitSize is not null, overwrite);

        var created = new List<string>();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] raw = ArrayPool<byte>.Shared.Rent(ChunkSectors * SectorSize);
        byte[] blocks = ArrayPool<byte>.Shared.Rent(ChunkSectors * SectorSize * 2);
        int[] lengths = new int[ChunkSectors];
        long totalOnDisk = 0;
        bool success = false;

        FileStream? part = null;
        var partIndex = new List<uint>();
        long partPos = 0;
        long partSectors = 0;

        void StartPart()
        {
            string path = splitSize is null ? fullOutput : SplitParts.PartPath(fullOutput, created.Count + 1);
            part = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20);
            created.Add(path);
            part.Write(new byte[32]); // header placeholder
            partPos = 32;
            partSectors = 0;
            partIndex.Clear();
        }

        void FinishPart()
        {
            CheckIndexRange(partPos);
            long indexOffset = partPos;
            var idx = new byte[(partIndex.Count + 1) * 4];
            for (int i = 0; i < partIndex.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(idx.AsSpan(i * 4), partIndex[i]);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(idx.AsSpan(partIndex.Count * 4), (uint)(partPos >> CompressedFormats.IndexAlignment));
            part!.Write(idx);

            var header = new byte[32];
            BinaryPrimitives.WriteUInt32LittleEndian(header, CompressedFormats.CciMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 32);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (ulong)(partSectors * SectorSize));
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)indexOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), SectorSize);
            header[28] = 1;
            header[29] = CompressedFormats.IndexAlignment;
            part.Position = 0;
            part.Write(header);
            totalOnDisk += part.Length;
            part.Dispose();
            part = null;
        }

        try
        {
            StartPart();
            long sector = 0;
            while (sector < sectorCount)
            {
                ct.ThrowIfCancellationRequested();
                int count = (int)Math.Min(ChunkSectors, sectorCount - sector);
                int bytes = CompressChunk(image, sector * SectorSize, count, raw, blocks, lengths, EncodeCci);
                sha.AppendData(raw, 0, bytes);
                for (int i = 0; i < count; i++)
                {
                    bool compressed = lengths[i] > 0;
                    var data = compressed
                        ? blocks.AsSpan(i * SectorSize * 2, lengths[i])
                        : raw.AsSpan(i * SectorSize, SectorSize);

                    // Same rule as Repackinator: start a new part before this block would push past the limit.
                    long projected = partPos + (partIndex.Count + 2) * 4L + data.Length;
                    if (projected > limit && partSectors > 0)
                    {
                        FinishPart();
                        StartPart();
                    }

                    partIndex.Add((uint)(partPos >> CompressedFormats.IndexAlignment) | (compressed ? 0x80000000u : 0u));
                    part!.Write(data);
                    partPos += data.Length;
                    partSectors++;
                }
                sector += count;
                progress.Report(new OperationProgress(OperationStage.Compressing, sector * SectorSize, image.Length));
            }
            FinishPart();

            if (splitSize is not null && created.Count == 1)
            {
                File.Move(created[0], fullOutput);
                created[0] = fullOutput;
            }
            success = true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
            ArrayPool<byte>.Shared.Return(blocks);
            if (!success)
            {
                part?.Dispose();
                foreach (var f in created)
                {
                    try
                    {
                        File.Delete(f);
                    }
                    catch (IOException)
                    {
                        // Best effort.
                    }
                }
            }
        }
        return new WriteResult(created, image.Length, totalOnDisk, Convert.ToHexString(sha.GetHashAndReset()));
    }

    private static void CheckIndexRange(long position)
    {
        if ((position >> CompressedFormats.IndexAlignment) > 0x7FFFFFFF)
        {
            throw new InvalidOperationException("Compressed image is too large for a single part; enable splitting.");
        }
    }
}
