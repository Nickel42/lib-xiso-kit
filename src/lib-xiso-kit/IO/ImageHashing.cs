using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using LibXisoKit.Identity;

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

/// <summary>
/// Passes reads through to a plain file source and, on the side, hashes that source front to back exactly as
/// <see cref="FileHashes.Compute"/> does (CRC32, MD5 and SHA-1 of the files as stored). Bytes the reader skips
/// (the video partition, scrubbed gaps, the unused tail) are read by the tee itself, so a reader that walks the
/// source forward, like the trim-mode writer, gets the hashes from a single pass over the disc instead of two.
/// Reads that go backwards are still served, just not hashed twice. Hashing runs on a worker, a few blocks
/// behind the reader.
/// </summary>
internal sealed class HashingTeeSource : ImageSource
{
    private readonly ImageSource _inner;
    private readonly object _gate = new();
    private readonly BlockingCollection<(byte[] Buffer, int Length)> _queue = new(boundedCapacity: 3);
    private readonly CancellationTokenSource _stop;
    private readonly Crc32 _crc = new();
    private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    private readonly IncrementalHash _sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    private readonly Task _worker;
    private long _hashed;
    private bool _finished;

    public HashingTeeSource(ImageSource inner, CancellationToken cancellationToken)
    {
        _inner = inner;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Files = inner.Files;
        _worker = Task.Factory.StartNew(Consume, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public override long Length => _inner.Length;

    public override ContainerFormat Format => _inner.Format;

    private void Consume()
    {
        try
        {
            foreach (var (buffer, n) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    Parallel.Invoke(
                        () => _crc.Append(buffer.AsSpan(0, n)),
                        () => _md5.AppendData(buffer, 0, n),
                        () => _sha1.AppendData(buffer, 0, n));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch
        {
            _stop.Cancel(); // unblock a reader waiting for queue space
            throw;
        }
    }

    public override int Read(long position, Span<byte> buffer)
    {
        lock (_gate)
        {
            if (!_finished && position > _hashed)
            {
                CatchUp(position, null);
            }
            int n = _inner.Read(position, buffer);
            long end = position + n;
            if (!_finished && end > _hashed && position <= _hashed)
            {
                var fresh = buffer[(int)(_hashed - position)..n];
                while (fresh.Length > 0)
                {
                    int take = Math.Min(fresh.Length, ImageHashing.BlockSize);
                    byte[] copy = ArrayPool<byte>.Shared.Rent(take);
                    fresh[..take].CopyTo(copy);
                    Enqueue(copy, take);
                    fresh = fresh[take..];
                }
                _hashed = end;
            }
            return n;
        }
    }

    /// <summary>Reads and hashes the source from where hashing stopped up to <paramref name="target"/>.</summary>
    private void CatchUp(long target, ThrottledProgress? progress)
    {
        while (_hashed < target)
        {
            byte[] block = ArrayPool<byte>.Shared.Rent(ImageHashing.BlockSize);
            int n;
            try
            {
                _stop.Token.ThrowIfCancellationRequested();
                n = _inner.Read(_hashed, block.AsSpan(0, (int)Math.Min(ImageHashing.BlockSize, target - _hashed)));
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(block);
                throw;
            }
            if (n <= 0)
            {
                ArrayPool<byte>.Shared.Return(block);
                break;
            }
            Enqueue(block, n);
            _hashed += n;
            progress?.Report(new OperationProgress(OperationStage.Hashing, _hashed, _inner.Length));
        }
    }

    /// <summary>Hands <paramref name="block"/> to the worker, which returns it to the pool.</summary>
    private void Enqueue(byte[] block, int length)
    {
        try
        {
            _queue.Add((block, length), _stop.Token);
        }
        catch (Exception ex)
        {
            ArrayPool<byte>.Shared.Return(block);
            if (ex is OperationCanceledException && _worker.IsFaulted)
            {
                _worker.GetAwaiter().GetResult(); // surface the hashing failure, not the cancellation
            }
            throw;
        }
    }

    /// <summary>Hashes whatever the reader skipped at the end and returns the hashes of the whole source.</summary>
    public FileHashes Finish(IProgress<OperationProgress>? progress)
    {
        lock (_gate)
        {
            CatchUp(_inner.Length, new ThrottledProgress(progress));
            _finished = true;
        }
        _queue.CompleteAdding();
        _worker.GetAwaiter().GetResult();
        return new FileHashes(
            _hashed,
            _crc.Value.ToString("x8"),
            Convert.ToHexString(_md5.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(_sha1.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>Stops the worker. The inner source belongs to the caller and stays open.</summary>
    public override void Dispose()
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.CompleteAdding();
        }
        try
        {
            _worker.Wait();
        }
        catch (AggregateException)
        {
            // Already failing; the hashes are not used.
        }
        while (_queue.TryTake(out var left))
        {
            ArrayPool<byte>.Shared.Return(left.Buffer);
        }
        _queue.Dispose();
        _md5.Dispose();
        _sha1.Dispose();
        _stop.Dispose();
        base.Dispose();
    }
}
