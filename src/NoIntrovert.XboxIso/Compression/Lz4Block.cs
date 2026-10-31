using System.Buffers.Binary;

namespace NoIntrovert.XboxIso.Compression;

/// <summary>
/// Minimal LZ4 raw-block codec (no frame format). Output is standard LZ4 block data
/// readable by any LZ4 implementation; the compressor is a fast greedy single-hash matcher,
/// which is ample for 2 KiB disc sectors.
/// </summary>
public static class Lz4Block
{
    private const int MinMatch = 4;
    private const int LastLiterals = 5;
    private const int MfLimit = 12;
    private const int MaxDistance = 65535;
    private const int HashLog = 12;

    /// <summary>Worst-case compressed size for an input of <paramref name="inputLength"/> bytes.</summary>
    public static int MaxCompressedLength(int inputLength) => inputLength + (inputLength / 255) + 16;

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="destination"/>.
    /// Returns the compressed length, or -1 if it does not fit in <paramref name="destination"/>.
    /// </summary>
    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int n = source.Length;
        var w = new Writer(destination);
        if (n < MfLimit + 1)
        {
            return w.EmitLastLiterals(source, 0) ? w.Position : -1;
        }

        Span<int> table = stackalloc int[1 << HashLog]; // stores position + 1, 0 = empty
        table.Clear();

        int anchor = 0;
        int ip = 0;
        int limit = n - MfLimit;
        int matchLimit = n - LastLiterals;

        while (ip < limit)
        {
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(ip, 4));
            int h = (int)((seq * 2654435761u) >> (32 - HashLog));
            int candidate = table[h] - 1;
            table[h] = ip + 1;

            if (candidate >= 0 && ip - candidate <= MaxDistance &&
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(candidate, 4)) == seq)
            {
                int matchLen = MinMatch;
                while (ip + matchLen < matchLimit && source[candidate + matchLen] == source[ip + matchLen])
                {
                    matchLen++;
                }

                while (ip > anchor && candidate > 0 && source[ip - 1] == source[candidate - 1])
                {
                    ip--;
                    candidate--;
                    matchLen++;
                }

                if (!w.EmitSequence(source.Slice(anchor, ip - anchor), ip - candidate, matchLen))
                {
                    return -1;
                }

                ip += matchLen;
                anchor = ip;
            }
            else
            {
                ip++;
            }
        }

        return w.EmitLastLiterals(source, anchor) ? w.Position : -1;
    }

    /// <summary>
    /// Decompresses a raw LZ4 block. Returns the number of bytes written.
    /// Throws <see cref="InvalidDataException"/> on malformed input.
    /// </summary>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int ip = 0;
        int op = 0;
        while (ip < source.Length)
        {
            byte token = source[ip++];
            int literalLength = token >> 4;
            if (literalLength == 15)
            {
                literalLength += ReadLength(source, ref ip);
            }

            if (ip + literalLength > source.Length || op + literalLength > destination.Length)
            {
                throw new InvalidDataException("LZ4: literal run exceeds buffer.");
            }
            source.Slice(ip, literalLength).CopyTo(destination[op..]);
            ip += literalLength;
            op += literalLength;

            if (ip >= source.Length)
            {
                break; // last sequence has no match part
            }

            if (ip + 2 > source.Length)
            {
                throw new InvalidDataException("LZ4: truncated match offset.");
            }
            int offset = source[ip] | (source[ip + 1] << 8);
            ip += 2;
            if (offset == 0 || offset > op)
            {
                throw new InvalidDataException("LZ4: invalid match offset.");
            }

            int matchLength = token & 15;
            if (matchLength == 15)
            {
                matchLength += ReadLength(source, ref ip);
            }
            matchLength += MinMatch;

            if (op + matchLength > destination.Length)
            {
                throw new InvalidDataException("LZ4: match exceeds output buffer.");
            }

            int from = op - offset;
            if (offset >= matchLength)
            {
                destination.Slice(from, matchLength).CopyTo(destination[op..]);
            }
            else if (offset == 1)
            {
                destination.Slice(op, matchLength).Fill(destination[from]); // run of one byte (zero filler)
            }
            else
            {
                // An overlapping match repeats the last `offset` bytes. Copy in growing non-overlapping
                // steps; each step is a multiple of `offset`, so the pattern stays in phase.
                for (int done = 0; done < matchLength;)
                {
                    int step = Math.Min(op + done - from, matchLength - done);
                    destination.Slice(from, step).CopyTo(destination[(op + done)..]);
                    done += step;
                }
            }
            op += matchLength;
        }
        return op;
    }

    private static int ReadLength(ReadOnlySpan<byte> source, ref int ip)
    {
        int len = 0;
        byte b;
        do
        {
            if (ip >= source.Length)
            {
                throw new InvalidDataException("LZ4: truncated length.");
            }
            b = source[ip++];
            len += b;
        }
        while (b == 255);
        return len;
    }

    private ref struct Writer
    {
        private readonly Span<byte> _dst;
        public int Position;

        public Writer(Span<byte> dst)
        {
            _dst = dst;
            Position = 0;
        }

        public bool EmitSequence(ReadOnlySpan<byte> literals, int offset, int matchLength)
        {
            int lit = literals.Length;
            int m = matchLength - MinMatch;
            int needed = 1 + (lit >= 15 ? (lit - 15) / 255 + 1 : 0) + lit + 2 + (m >= 15 ? (m - 15) / 255 + 1 : 0);
            if (Position + needed > _dst.Length)
            {
                return false;
            }

            int tokenPos = Position++;
            byte token = (byte)(Math.Min(lit, 15) << 4);
            if (lit >= 15)
            {
                WriteLength(lit - 15);
            }
            literals.CopyTo(_dst[Position..]);
            Position += lit;
            _dst[Position++] = (byte)offset;
            _dst[Position++] = (byte)(offset >> 8);
            token |= (byte)Math.Min(m, 15);
            if (m >= 15)
            {
                WriteLength(m - 15);
            }
            _dst[tokenPos] = token;
            return true;
        }

        public bool EmitLastLiterals(ReadOnlySpan<byte> source, int anchor)
        {
            int lit = source.Length - anchor;
            int needed = 1 + (lit >= 15 ? (lit - 15) / 255 + 1 : 0) + lit;
            if (Position + needed > _dst.Length)
            {
                return false;
            }
            _dst[Position++] = (byte)(Math.Min(lit, 15) << 4);
            if (lit >= 15)
            {
                WriteLength(lit - 15);
            }
            source[anchor..].CopyTo(_dst[Position..]);
            Position += lit;
            return true;
        }

        private void WriteLength(int v)
        {
            while (v >= 255)
            {
                _dst[Position++] = 255;
                v -= 255;
            }
            _dst[Position++] = (byte)v;
        }
    }
}
