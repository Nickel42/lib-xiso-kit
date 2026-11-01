using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using XboxIsoLibrary.Compression;

namespace XboxIsoLibrary.IO;

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
    private const int MaxBlock = SectorSize * 2;

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

    /// <summary>Compressed form of one sector (or -1 when it should be stored raw).</summary>
    private delegate int Encoder(ReadOnlySpan<byte> sector, Span<byte> scratch, Span<byte> block);

    /// <summary>One batch of input sectors and, when compressing, their encoded blocks.</summary>
    private sealed class Chunk : IDisposable
    {
        public readonly byte[] Raw = ArrayPool<byte>.Shared.Rent(ChunkSectors * SectorSize);
        public readonly byte[]? Blocks;
        public readonly int[] Lengths = new int[ChunkSectors];
        public int Count;
        public int Bytes;

        public Chunk(bool compressed) => Blocks = compressed ? ArrayPool<byte>.Shared.Rent(ChunkSectors * MaxBlock) : null;

        public ReadOnlySpan<byte> Data(int i) => Lengths[i] > 0
            ? Blocks.AsSpan(i * MaxBlock, Lengths[i])
            : Raw.AsSpan(i * SectorSize, SectorSize);

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(Raw);
            if (Blocks is not null)
            {
                ArrayPool<byte>.Shared.Return(Blocks);
            }
        }
    }

    /// <summary>
    /// Streams the image through <paramref name="consume"/> one chunk at a time. The next chunk is read
    /// (and compressed, when <paramref name="encoder"/> is set) on a worker while the current one is consumed,
    /// so reading, compressing and writing overlap instead of taking turns.
    /// </summary>
    private static void ForEachChunk(ImageSource image, Encoder? encoder, CancellationToken ct, Action<Chunk, long> consume)
    {
        long sectorCount = image.Length / SectorSize;
        if (sectorCount == 0)
        {
            return;
        }

        using var a = new Chunk(encoder is not null);
        using var b = new Chunk(encoder is not null);
        Task? pending = null;
        Task Load(Chunk c, long sector) => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            c.Count = (int)Math.Min(ChunkSectors, sectorCount - sector);
            c.Bytes = c.Count * SectorSize;
            image.ReadExactly(sector * SectorSize, c.Raw.AsSpan(0, c.Bytes));
            if (encoder is not null)
            {
                Parallel.For(0, c.Count, () => new byte[Lz4Block.MaxCompressedLength(SectorSize)], (i, _, scratch) =>
                {
                    c.Lengths[i] = encoder(c.Raw.AsSpan(i * SectorSize, SectorSize), scratch, c.Blocks.AsSpan(i * MaxBlock, MaxBlock));
                    return scratch;
                }, _ => { });
            }
        });

        try
        {
            Chunk current = a, next = b;
            long sector = 0;
            pending = Load(current, 0);
            while (true)
            {
                pending.GetAwaiter().GetResult();
                pending = null;
                long first = sector;
                sector += current.Count;
                if (sector < sectorCount)
                {
                    pending = Load(next, sector);
                }
                ct.ThrowIfCancellationRequested();
                consume(current, first);
                if (pending is null)
                {
                    break;
                }
                (current, next) = (next, current);
            }
        }
        finally
        {
            // The worker may still be filling a buffer; wait before the buffers go back to the pool.
            if (pending is not null)
            {
                try
                {
                    pending.Wait();
                }
                catch (AggregateException)
                {
                    // Already failing or cancelled; the original exception is the one that matters.
                }
            }
        }
    }

    /// <summary>Hashes <paramref name="chunk"/> on a worker while <paramref name="write"/> runs.</summary>
    private static void HashWhile(IncrementalHash sha, Chunk chunk, Action write)
    {
        var hash = Task.Run(() => sha.AppendData(chunk.Raw, 0, chunk.Bytes));
        try
        {
            write();
        }
        finally
        {
            hash.GetAwaiter().GetResult();
        }
    }

    private static WriteResult WriteIso(ImageSource image, string outputPath, long? splitSize, bool overwrite, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        using var writer = new SplitFileWriter(outputPath, splitSize, overwrite);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        ForEachChunk(image, null, ct, (c, sector) =>
        {
            long pos = sector * SectorSize;
            HashWhile(sha, c, () => writer.Write(pos, c.Raw.AsSpan(0, c.Bytes)));
            progress.Report(new OperationProgress(OperationStage.Writing, pos + c.Bytes, image.Length));
        });
        long written = writer.BytesWritten;
        var files = writer.Complete();
        return new WriteResult(files, image.Length, written, Convert.ToHexString(sha.GetHashAndReset()));
    }

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
        // A chunk's blocks are gathered here (with their alignment padding) and written in one call.
        byte[] staging = ArrayPool<byte>.Shared.Rent(ChunkSectors * (SectorSize + align));
        try
        {
            ForEachChunk(image, EncodeCso, ct, (c, sector) =>
            {
                long chunkStart = position;
                int len = 0;
                for (int i = 0; i < c.Count; i++)
                {
                    int misalign = (int)((chunkStart + len) & (align - 1));
                    if (misalign != 0)
                    {
                        staging.AsSpan(len, align - misalign).Clear();
                        len += align - misalign;
                    }
                    index[sector + i] = (uint)((chunkStart + len) >> CompressedFormats.IndexAlignment) | (c.Lengths[i] > 0 ? 0x80000000u : 0u);
                    var data = c.Data(i);
                    data.CopyTo(staging.AsSpan(len));
                    len += data.Length;
                }
                HashWhile(sha, c, () => writer.Write(chunkStart, staging.AsSpan(0, len)));
                position = chunkStart + len;
                progress.Report(new OperationProgress(OperationStage.Compressing, (sector + c.Count) * SectorSize, image.Length));
            });

            // Align the end so the final index entry (end of the last block) is exact.
            int tailMisalign = (int)(position & (align - 1));
            if (tailMisalign != 0)
            {
                writer.Write(position, new byte[align - tailMisalign]);
                position += align - tailMisalign;
            }
            CheckIndexRange(position);
            index[sectorCount] = (uint)(position >> CompressedFormats.IndexAlignment);

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
            ArrayPool<byte>.Shared.Return(staging);
        }
        long written = writer.BytesWritten;
        var files = writer.Complete();
        return new WriteResult(files, image.Length, written, Convert.ToHexString(sha.GetHashAndReset()));
    }

    private static WriteResult WriteCci(ImageSource image, string outputPath, long? splitSize, bool overwrite, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        long limit = splitSize ?? long.MaxValue;
        string fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        SplitFileWriter.PrepareTargets(fullOutput, splitSize is not null, overwrite);

        var created = new List<string>();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        long totalOnDisk = 0;
        bool success = false;

        FileStream? part = null;
        var partIndex = new List<uint>();
        long partPos = 0;
        long partSectors = 0;

        void StartPart()
        {
            string path = splitSize is null ? fullOutput : SplitParts.PartPath(fullOutput, created.Count + 1);
            part = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 22);
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
            ForEachChunk(image, EncodeCci, ct, (c, sector) =>
            {
                HashWhile(sha, c, () =>
                {
                    for (int i = 0; i < c.Count; i++)
                    {
                        var data = c.Data(i);

                        // Same rule as Repackinator: start a new part before this block would push past the limit.
                        long projected = partPos + (partIndex.Count + 2) * 4L + data.Length;
                        if (projected > limit && partSectors > 0)
                        {
                            FinishPart();
                            StartPart();
                        }

                        partIndex.Add((uint)(partPos >> CompressedFormats.IndexAlignment) | (c.Lengths[i] > 0 ? 0x80000000u : 0u));
                        part!.Write(data);
                        partPos += data.Length;
                        partSectors++;
                    }
                });
                progress.Report(new OperationProgress(OperationStage.Compressing, (sector + c.Count) * SectorSize, image.Length));
            });
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
