using System.Buffers.Binary;
using LibXisoKit.Compression;

namespace LibXisoKit.IO;

/// <summary>Opens any supported image file (ISO, split ISO, CCI, CSO) as a logical <see cref="ImageSource"/>.</summary>
public static class ImageOpener
{
    /// <summary>Detects the container of <paramref name="path"/> (any part of a split set) and opens it.</summary>
    public static ImageSource Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Image not found.", path);
        }

        var parts = SplitParts.Resolve(path);
        return DetectFormat(parts[0]) switch
        {
            ContainerFormat.Cci => new CciSource(parts),
            ContainerFormat.Cso => new CsoSource(parts),
            _ => new ConcatenatedFileSource(parts),
        };
    }

    /// <summary>Detects the container format from the file's magic bytes.</summary>
    public static ContainerFormat DetectFormat(string path)
    {
        Span<byte> magic = stackalloc byte[4];
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (RandomAccess.Read(handle, magic, 0) < 4)
        {
            return ContainerFormat.Iso;
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(magic) switch
        {
            CompressedFormats.CciMagic => ContainerFormat.Cci,
            CompressedFormats.CsoMagic => ContainerFormat.Cso,
            _ => ContainerFormat.Iso,
        };
    }
}
