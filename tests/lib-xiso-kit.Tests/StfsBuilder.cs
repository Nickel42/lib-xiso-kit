using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LibXisoKit.Stfs;

namespace LibXisoKit.Tests;

/// <summary>
/// Builds synthetic STFS packages for the tests. The layout is laid down block by block in the order the format
/// describes (a level-0 table before every 170 data blocks, the level-1 table after the first 170, the level-2
/// table after the first 28,900), not from the reader's address formulas, so the two check each other.
/// </summary>
internal static class StfsBuilder
{
    public sealed record Item(string Name, int Parent, bool Directory = false, byte[]? Data = null, bool Contiguous = true);

    private const int Block = 0x1000;
    private const int PerTable = 170;
    private const int Level1Span = PerTable * PerTable;
    private const uint HeaderSize = 0x971A; // first table at 0xA000

    /// <summary>
    /// A package holding <paramref name="items"/> (Parent: index of the folder in <paramref name="items"/>, or -1).
    /// With two table copies (block separation bit 0 clear), odd-numbered lower tables use their second copy and
    /// the unused copies are filled with junk, so a reader that picks the wrong copy fails.
    /// </summary>
    public static byte[] Build(IReadOnlyList<Item> items, byte blockSeparation = 1, XContentType type = XContentType.ArcadeTitle,
        uint titleId = 0x58410A5D, string displayName = "Test Game", string magic = "LIVE")
    {
        int copies = (blockSeparation & 1) == 0 ? 2 : 1;
        var rng = new Random(1234);

        // Logical data blocks: the file table at block 0, then each file's blocks.
        var blocks = new List<byte[]> { new byte[Block] };
        var next = new List<int> { 0xFFFFFF };
        var starts = new int[items.Count];
        var counts = new int[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var data = items[i].Data ?? [];
            int count = (data.Length + Block - 1) / Block;
            counts[i] = count;
            if (items[i].Directory || count == 0)
            {
                continue;
            }
            int first = blocks.Count;
            for (int k = 0; k < count; k++)
            {
                blocks.Add(Slice(data, k));
                next.Add(0xFFFFFF);
            }
            if (items[i].Contiguous)
            {
                starts[i] = first;
                for (int k = 0; k < count - 1; k++)
                {
                    next[first + k] = first + k + 1;
                }
            }
            else
            {
                // Stored back to front: the file's first block is the last one reserved.
                var order = Enumerable.Range(0, count).Select(k => first + count - 1 - k).ToArray();
                for (int k = 0; k < count; k++)
                {
                    blocks[order[k]] = Slice(data, k);
                }
                starts[i] = order[0];
                for (int k = 0; k < count - 1; k++)
                {
                    next[order[k]] = order[k + 1];
                }
            }
        }

        // The file table.
        var table = blocks[0];
        for (int i = 0; i < items.Count; i++)
        {
            var e = table.AsSpan(i * 0x40, 0x40);
            var name = Encoding.ASCII.GetBytes(items[i].Name);
            name.CopyTo(e);
            e[0x28] = (byte)(name.Length | (items[i].Contiguous ? 0x40 : 0) | (items[i].Directory ? 0x80 : 0));
            Int24Le(e[0x29..], counts[i]);
            Int24Le(e[0x2C..], counts[i]);
            Int24Le(e[0x2F..], starts[i]);
            BinaryPrimitives.WriteInt16BigEndian(e[0x32..], (short)items[i].Parent);
            BinaryPrimitives.WriteUInt32BigEndian(e[0x34..], (uint)(items[i].Data?.Length ?? 0));
        }

        int n = blocks.Count;
        int top = n <= PerTable ? 0 : n <= Level1Span ? 1 : 2;

        // Hash tables, bottom up; each lower table's current copy is 0 or 1 (odd index → 1 with two copies).
        int Active(int index) => copies == 2 ? index & 1 : 0;
        var level0 = BuildLevel(n, PerTable, b => blocks[b], b => next[b], _ => 0x80);
        var level1 = top >= 1 ? BuildLevel(level0.Count, PerTable, t => level0[t], _ => 0, t => (byte)(0x80 | (Active(t) << 6))) : [];
        var level2 = top >= 2 ? BuildLevel(level1.Count, PerTable, t => level1[t], _ => 0, t => (byte)(0x80 | (Active(t) << 6))) : [];
        var topTable = top switch { 0 => level0[0], 1 => level1[0], _ => level2[0] };
        int topCopy = copies == 2 ? (blockSeparation & 2) >> 1 : 0;

        // Lay the blocks down in file order.
        var body = new List<byte[]>();
        void Emit(byte[] current, int active)
        {
            for (int c = 0; c < copies; c++)
            {
                body.Add(c == active ? current : Junk(rng));
            }
        }
        for (int b = 0; b < n; b++)
        {
            if (b % PerTable == 0)
            {
                if (b == Level1Span && top == 2)
                {
                    Emit(level2[0], topCopy);
                }
                if (top >= 1 && (b == PerTable || (b > 0 && b % Level1Span == 0)))
                {
                    int index = b == PerTable ? 0 : b / Level1Span;
                    Emit(level1[index], top == 1 ? topCopy : Active(index));
                }
                int i0 = b / PerTable;
                Emit(level0[i0], top == 0 ? topCopy : Active(i0));
            }
            body.Add(blocks[b]);
        }

        var header = new byte[0xA000];
        Encoding.ASCII.GetBytes(magic).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x340), HeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x344), (uint)type);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x348), 2);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x34C), (ulong)body.Count * Block);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x360), titleId);
        var vd = header.AsSpan(StfsVolumeDescriptor, 0x24);
        vd[0] = 0x24;
        vd[2] = blockSeparation;
        BinaryPrimitives.WriteUInt16LittleEndian(vd[3..], 1);
        Int24Le(vd[5..], 0);
        SHA1.HashData(topTable).CopyTo(vd[8..]);
        BinaryPrimitives.WriteUInt32BigEndian(vd[0x1C..], (uint)n);
        Encoding.BigEndianUnicode.GetBytes(displayName).CopyTo(header, 0x411);
        Encoding.BigEndianUnicode.GetBytes("Test Publisher").CopyTo(header, 0x1611);

        var output = new byte[header.Length + body.Count * Block];
        header.CopyTo(output, 0);
        for (int i = 0; i < body.Count; i++)
        {
            body[i].CopyTo(output, header.Length + i * Block);
        }
        return output;
    }

    /// <summary>An SVOD (Games on Demand) header with no data.</summary>
    public static byte[] SvodHeader(uint titleId)
    {
        var header = new byte[0xB000];
        Encoding.ASCII.GetBytes("LIVE").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x340), 0xAD0E);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x344), (uint)XContentType.GamesOnDemand);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x360), titleId);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x39D), 3);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x3A9), 1);
        Encoding.BigEndianUnicode.GetBytes("A Disc Game").CopyTo(header, 0x411);
        return header;
    }

    /// <summary>The file offset of data block <paramref name="index"/> in a package built with one table copy and ≤170 blocks.</summary>
    public static int SmallDataOffset(int index) => 0xA000 + (index + 1) * Block;

    private const int StfsVolumeDescriptor = 0x379;

    /// <summary>Hash tables over <paramref name="count"/> children: SHA-1, status, next block (big-endian 24-bit).</summary>
    private static List<byte[]> BuildLevel(int count, int perTable, Func<int, byte[]> child, Func<int, int> nextOf, Func<int, byte> status)
    {
        var tables = new List<byte[]>();
        for (int start = 0; start < count; start += perTable)
        {
            var t = new byte[Block];
            for (int i = start; i < Math.Min(count, start + perTable); i++)
            {
                var e = t.AsSpan((i - start) * 0x18, 0x18);
                SHA1.HashData(child(i)).CopyTo(e);
                e[20] = status(i);
                int nb = nextOf(i);
                e[21] = (byte)(nb >> 16);
                e[22] = (byte)(nb >> 8);
                e[23] = (byte)nb;
            }
            tables.Add(t);
        }
        return tables;
    }

    private static readonly byte[] Zeros = new byte[Block];

    /// <summary>Block <paramref name="k"/> of <paramref name="data"/>; all-zero blocks share one array, so big packages stay small in memory.</summary>
    private static byte[] Slice(byte[] data, int k)
    {
        var part = data.AsSpan(k * Block, Math.Min(Block, data.Length - k * Block));
        if (part.Length == Block && !part.ContainsAnyExcept((byte)0))
        {
            return Zeros;
        }
        var b = new byte[Block];
        part.CopyTo(b);
        return b;
    }

    private static byte[] Junk(Random rng)
    {
        var b = new byte[Block];
        rng.NextBytes(b);
        return b;
    }

    private static void Int24Le(Span<byte> s, int v)
    {
        s[0] = (byte)v;
        s[1] = (byte)(v >> 8);
        s[2] = (byte)(v >> 16);
    }
}
