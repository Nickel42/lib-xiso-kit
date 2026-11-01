using LibXisoKit.IO;
using LibXisoKit.Xbe;

namespace LibXisoKit.Xdvdfs;

/// <summary>Result of <see cref="PartitionTrimmer.Build"/>.</summary>
public sealed record TrimResult(VirtualImage Image, long ScrubbedBytes, int PatchedExecutables);

/// <summary>
/// Produces an XISO by copying the game partition exactly as it is laid out on the disc, dropping the video
/// partition and everything after the last used sector. Every file keeps its original sector, which makes
/// this the most conservative way to shrink an image. Optionally zero-fills unused sectors ("scrub") so the
/// result compresses well; Redump images fill those gaps with random data.
/// </summary>
public static class PartitionTrimmer
{
    public static TrimResult Build(XdvdfsVolume volume, bool scrub, bool mediaPatch)
    {
        long endSector = volume.UsedEndSector;
        var partition = new SubrangeSource(volume.Source, volume.PartitionOffset, volume.PartitionLength);

        // 1. Sector intervals that must be copied.
        var used = new List<(long Start, long End)>();
        if (scrub)
        {
            used.Add((0, XdvdfsConstants.DescriptorSector + 1)); // header area + descriptor
            if (volume.RootSize > 0)
            {
                used.Add((volume.RootSector, volume.RootSector + XdvdfsConstants.SectorsFor(volume.RootSize)));
            }
            foreach (var e in volume.EnumerateEntries())
            {
                if (e.Size > 0)
                {
                    used.Add((e.StartSector, e.StartSector + e.SectorCount));
                }
            }
        }
        else
        {
            used.Add((0, endSector));
        }
        used = Merge(used);

        // 2. Executables replaced by patched copies (whole sectors, so tails stay byte-identical).
        var special = new List<(long Start, long End, byte[] Data)>();
        int patched = 0;
        if (mediaPatch)
        {
            var seen = new HashSet<uint>();
            foreach (var f in volume.EnumerateFiles())
            {
                if (f.Size == 0 || !XbeMediaPatch.AppliesTo(f.Name) || !seen.Add(f.StartSector))
                {
                    continue;
                }
                long bytes = f.SectorCount * XdvdfsConstants.SectorSize;
                var data = new byte[bytes];
                partition.ReadExactly((long)f.StartSector * XdvdfsConstants.SectorSize, data);
                if (XbeMediaPatch.Apply(data.AsSpan(0, (int)f.Size)) > 0)
                {
                    patched++;
                    special.Add((f.StartSector, f.StartSector + f.SectorCount, data));
                }
            }
            special.Sort((a, b) => a.Start.CompareTo(b.Start));
            // Drop any patched span overlapping a previous one (cross-linked files).
            for (int i = special.Count - 1; i > 0; i--)
            {
                if (special[i].Start < special[i - 1].End)
                {
                    special.RemoveAt(i);
                    patched--;
                }
            }
        }

        // 3. Emit copy segments for used intervals minus patched spans.
        var builder = new VirtualImage.Builder();
        long scrubbed = endSector * XdvdfsConstants.SectorSize;
        foreach (var (start, end) in used)
        {
            long s = start;
            foreach (var sp in special)
            {
                if (sp.End <= s || sp.Start >= end)
                {
                    continue;
                }
                if (sp.Start > s)
                {
                    AddCopy(builder, partition, s, sp.Start);
                }
                s = Math.Max(s, sp.End);
            }
            if (s < end)
            {
                AddCopy(builder, partition, s, end);
            }
            scrubbed -= (end - start) * XdvdfsConstants.SectorSize;
        }
        foreach (var sp in special)
        {
            builder.Add(sp.Start * XdvdfsConstants.SectorSize, sp.Data);
        }

        long length = endSector * XdvdfsConstants.SectorSize;
        length = (length + XdvdfsConstants.FileModulus - 1) / XdvdfsConstants.FileModulus * XdvdfsConstants.FileModulus;
        return new TrimResult(builder.Build(length), scrub ? Math.Max(0, scrubbed) : 0, patched);
    }

    private static void AddCopy(VirtualImage.Builder builder, ImageSource partition, long startSector, long endSector)
    {
        long start = startSector * XdvdfsConstants.SectorSize;
        long length = Math.Min(endSector * XdvdfsConstants.SectorSize, partition.Length) - start;
        if (length > 0)
        {
            builder.Add(start, length, ContentProvider.FromSource(partition, start));
        }
    }

    private static List<(long Start, long End)> Merge(List<(long Start, long End)> spans)
    {
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(long Start, long End)>();
        foreach (var s in spans)
        {
            if (merged.Count > 0 && s.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.End, s.End));
            }
            else
            {
                merged.Add(s);
            }
        }
        return merged;
    }
}
