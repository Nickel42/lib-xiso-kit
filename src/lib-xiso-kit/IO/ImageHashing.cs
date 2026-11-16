using System.Buffers;

namespace LibXisoKit.IO;

/// <summary>Streams whole images through hash functions.</summary>
internal static class ImageHashing
{
    internal const int BlockSize = 4 << 20;

    /// <summary>
    /// Feeds every byte of <paramref name="source"/> to <paramref name="append"/> in order. Double-buffered:
    /// the next block is read while <paramref name="append"/> runs on the current one, so reading and hashing
    /// overlap instead of taking turns.
    /// </summary>
    public static void Stream(ImageSource source, Action<byte[], int> append, OperationStage stage, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var throttled = new ThrottledProgress(progress);
        byte[] reading = ArrayPool<byte>.Shared.Rent(BlockSize);
        byte[] hashing = ArrayPool<byte>.Shared.Rent(BlockSize);
        Task pending = Task.CompletedTask;
        try
        {
            long pos = 0;
            long len = source.Length;
            while (pos < len)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(BlockSize, len - pos);
                source.ReadExactly(pos, reading.AsSpan(0, want));
                pending.GetAwaiter().GetResult();
                (reading, hashing) = (hashing, reading);
                byte[] chunk = hashing;
                pending = Task.Run(() => append(chunk, want));
                pos += want;
                throttled.Report(new OperationProgress(stage, pos, len));
            }
            pending.GetAwaiter().GetResult();
        }
        finally
        {
            // The worker may still be hashing a buffer; wait before the buffers go back to the pool.
            try
            {
                pending.Wait();
            }
            catch (AggregateException)
            {
                // Already failing or cancelled; the original exception is the one that matters.
            }
            ArrayPool<byte>.Shared.Return(reading);
            ArrayPool<byte>.Shared.Return(hashing);
        }
    }
}
