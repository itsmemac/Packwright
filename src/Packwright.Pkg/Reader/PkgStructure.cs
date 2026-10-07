// Packwright.Pkg - a plain-text description of a package's structure, for comparing packages byte by byte
// when writing or debugging a package builder.
#nullable enable
using System.Buffers.Binary;
using System.Text;
using LibProsperoPkg.PFS;
using LibProsperoPkg.Util;

namespace Packwright.Pkg.Reader;

public sealed partial class PkgPackage
{
    /// <summary>Describes the package layout: headers, entry table, outer image, layout counts and inner image.</summary>
    public string DescribeStructure()
    {
        var sb = new StringBuilder();
        void Line(string text = "") => sb.AppendLine(text);

        // ---- FIH header and CNT header, as raw non-zero rows -----------------------------------------
        byte[] fih = new byte[0x100];
        byte[] cnt = new byte[CntHeaderSize];
        lock (_file)
        {
            _file.Position = 0; _file.ReadExactly(fih);
            _file.Position = _cntOffset; _file.ReadExactly(cnt);
        }
        Line($"file size 0x{FileSize:X}  FIH signed byte 0x{SignedByte:X2}  FIH version {FormatVersion}");
        Line($"PFS offset 0x{PfsOffset:X} size 0x{PfsSize:X}; superblock at 0x{SuperblockOffset:X}; CNT at 0x{CntOffset:X}");
        Line($"CNT: flags 0x{HeaderFlags:X8} entries {Entries.Count} sc-entries {ScEntryCount} body 0x{BodyOffset:X}+0x{BodySize:X}");
        Line($"CNT: content id {ContentId} drm {DrmType} type {ContentType} content flags 0x{ContentFlags:X8}");
        Line("FIH rows (non-zero):");
        Rows(sb, fih);
        Line("CNT header rows (non-zero):");
        Rows(sb, cnt);

        // ---- entries -----------------------------------------------------------------------------------
        Line("entries:");
        foreach (PkgEntry e in Entries)
            Line($"  0x{e.Id:X4} {(e.IsEncrypted ? "enc" : "   ")} f1=0x{e.Flags1:X8} f2=0x{e.Flags2:X8} off=0x{e.Offset:X} size=0x{e.Size:X} name='{e.Name}'");

        // ---- outer image ---------------------------------------------------------------------------------
        if (IsMetadataOnly || IsRetail) return sb.ToString();
        try
        {
            EnsureInner();
            ProsperoPfsReader outer = _outer!;
            byte[] head = new byte[0x400];
            long sbRel = SuperblockOffset - PfsOffset;
            new FileReader(_file, PfsOffset).Read(sbRel, head, 0, head.Length);
            ProsperoPfsHeader hdr = ProsperoPfsHeader.ReadFromStream(new MemoryStream(head));
            Line($"outer PFS: version {hdr.Version} mode {hdr.Mode} block 0x{hdr.BlockSize:X} dinodes {hdr.DinodeCount} dblocks {hdr.Ndblock} seed {Convert.ToHexString(hdr.Seed ?? [])} ({(_xtsReader is null ? "plain" : "encrypted")})");
            Line("outer files:");
            foreach (ProsperoPfsReader.File f in outer.GetAllFiles())
                Line($"  {f.name,-24} off=0x{f.offset:X} size=0x{f.size:X} logical/compressed=0x{f.compressed_size:X} flags={f.flags}");

            // ---- layout file -----------------------------------------------------------------------------
            byte[] naps = new byte[_layoutFile!.size];
            _layoutFile.GetView().Read(0, naps, 0, naps.Length);
            var layout = new NapsLayout(naps);
            Line($"layout: {naps.Length} bytes, files {layout.NumFiles}, ublocks {layout.NumUBlocks}, shuffle patterns {layout.NumShufflePatterns}, blocks {layout.Blocks.Count}, u2c {layout.U2c.Count}");
            Line($"layout file offsets: first {string.Join(", ", layout.Files.Take(4).Select(f => $"0x{f.UncompressedOffset:X}/t{f.Type:X2}"))} ... last {string.Join(", ", layout.Files.TakeLast(3).Select(f => $"0x{f.UncompressedOffset:X}/t{f.Type:X2}"))}");
            int rawBlocks = 0, krakenBlocks = 0, zeroish = 0;
            foreach (NapsBlockInfo b in layout.Blocks.Where(b => !b.IsRunBase && !b.IsTerminal))
            {
                if (b.KdePredictor != 0) continue;
                if (b.Even == 0 && b.Odd == 0) rawBlocks++; else krakenBlocks++;
            }
            _ = zeroish;
            Line($"layout normal blocks: {rawBlocks} with no compression flags, {krakenBlocks} with compression flags");

            // ---- inner image -----------------------------------------------------------------------------
            InnerMount mount = _mount!;
            Line($"inner image: logical size 0x{mount.MountSize:X}, superblock at 0x{mount.SuperblockOffset:X}, {Files.Count} files, {Files.Sum(f => f.Size):N0} bytes of file data");
            byte[] isb = new byte[0x80];
            mount.Position = mount.SuperblockOffset;
            mount.ReadExactly(isb);
            Line("inner superblock rows (non-zero):");
            Rows(sb, isb);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            Line("outer/inner not described: " + ex.Message);
        }
        return sb.ToString();
    }

    private static void Rows(StringBuilder sb, byte[] data)
    {
        for (int i = 0; i < data.Length; i += 32)
        {
            ReadOnlySpan<byte> row = data.AsSpan(i, Math.Min(32, data.Length - i));
            if (row.IndexOfAnyExcept((byte)0) < 0) continue;
            sb.AppendLine($"  {i:X4}: {Convert.ToHexString(row)}");
        }
    }
}

public sealed partial class PkgPackage
{
    /// <summary>Dumps the inner filesystem: superblock, every inode record and every directory listing.</summary>
    public string DescribeInnerFilesystem()
    {
        var sb = new StringBuilder();
        EnsureInner();
        InnerMount mount = _mount ?? throw new InvalidDataException("This package has no inner image.");
        long meta = mount.SuperblockOffset;

        byte[] head = new byte[0x400];
        mount.Position = meta; mount.ReadExactly(head);
        int blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x20));
        long inodeCount = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(0x30));
        sb.AppendLine($"inner superblock @0x{meta:X}: mode 0x{BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0x1C)):X} block 0x{blockSize:X} dinodes {inodeCount} nblock {BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(0x28))} ndblock {BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(0x38))} inode-blocks {BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(0x40))}");
        sb.AppendLine("superblock bytes (non-zero rows, full 0x400):");
        Rows(sb, head);

        int perBlock = blockSize / 0xA8;
        var inodes = new byte[inodeCount][];
        for (long i = 0; i < inodeCount; i++)
        {
            byte[] raw = new byte[0xA8];
            mount.Position = meta + blockSize + (i / perBlock) * blockSize + (i % perBlock) * 0xA8;
            mount.ReadExactly(raw);
            inodes[i] = raw;
        }
        sb.AppendLine("inodes: index mode nlink flags size sizeC | @0x60 u64 | @0x68 u32 | @0x6C u32 | tail");
        for (long i = 0; i < inodeCount; i++)
        {
            byte[] r = inodes[i];
            sb.AppendLine($"  [{i,3}] mode=0x{BinaryPrimitives.ReadUInt16LittleEndian(r):X4} nlink={BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(2))} flags=0x{BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(4)):X} size={BinaryPrimitives.ReadInt64LittleEndian(r.AsSpan(8))} sizeC={BinaryPrimitives.ReadInt64LittleEndian(r.AsSpan(0x10))} | t=0x{BinaryPrimitives.ReadInt64LittleEndian(r.AsSpan(0x18)):X} uid={BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(0x48))} gid={BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(0x4C))} unk1=0x{BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(0x50)):X} unk2=0x{BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(0x58)):X} | @60=0x{BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(0x60)):X} @68={BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(0x68))} @6C={BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(0x6C))} | {Convert.ToHexString(r.AsSpan(0x70))}");
        }

        sb.AppendLine("directories:");
        for (long i = 0; i < inodeCount; i++)
        {
            byte[] r = inodes[i];
            if ((BinaryPrimitives.ReadUInt16LittleEndian(r) & 0x4000) == 0) continue;
            long size = BinaryPrimitives.ReadInt64LittleEndian(r.AsSpan(8));
            long where = (long)BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(0x60));
            if (size <= 0 || size > 64 * 1024 * 1024) continue;
            byte[] listing = new byte[size];
            mount.Position = where; mount.ReadExactly(listing);
            sb.AppendLine($"  dir inode {i} at 0x{where:X}, {size} bytes:");
            using var ms = new MemoryStream(listing);
            while (ms.Position + 0x10 <= ms.Length)
            {
                long pos = ms.Position;
                ProsperoPfsDirent d;
                try { d = ProsperoPfsDirent.ReadFromStream(ms); } catch (Exception) { break; }
                if (d.EntSize <= 0) break;
                ms.Position = pos + d.EntSize;
                sb.AppendLine($"    @{pos,5} ino={d.InodeNumber,3} type={d.Type} entsize={d.EntSize} '{d.Name}'");
            }
        }
        return sb.ToString();
    }
}

public sealed partial class PkgPackage
{
    /// <summary>Reads a byte range of the expanded inner image (for diagnostics and comparisons).</summary>
    public byte[] ReadInnerRange(long offset, int count)
    {
        EnsureInner();
        InnerMount mount = _mount ?? throw new InvalidDataException("This package has no inner image.");
        byte[] data = new byte[count];
        mount.Position = offset;
        mount.ReadExactly(data);
        return data;
    }
}

public sealed partial class PkgPackage
{
    /// <summary>The package's layout file (naps_pkg_layout.dat), parsed, for diagnostics.</summary>
    internal NapsLayout ReadLayout()
    {
        EnsureInner();
        byte[] naps = new byte[_layoutFile!.size];
        _layoutFile.GetView().Read(0, naps, 0, naps.Length);
        return new NapsLayout(naps);
    }
}

public sealed partial class PkgPackage
{
    /// <summary>Reads a range of the stored inner image (pfs_image.dat), for diagnostics.</summary>
    internal byte[] ReadStoredRange(long offset, int count)
    {
        EnsureInner();
        byte[] data = new byte[count];
        _innerFile!.GetView().Read(offset, data, 0, count);
        return data;
    }

    /// <summary>The raw outer-block records of the layout file (8 bytes each), for diagnostics.</summary>
    internal byte[][] ReadOuterBlockRecords()
    {
        EnsureInner();
        byte[] naps = new byte[_layoutFile!.size];
        _layoutFile.GetView().Read(0, naps, 0, naps.Length);
        ulong w1 = BinaryPrimitives.ReadUInt64LittleEndian(naps.AsSpan(8));
        int count = (int)(w1 & 0xFFFFFF);
        var records = new byte[count][];
        for (int i = 0; i < count; i++) records[i] = naps.AsSpan(16 + 8 * i, 8).ToArray();
        return records;
    }
}

public sealed partial class PkgPackage
{
    /// <summary>Dumps the outer filesystem: superblock fields, the inode table and what lies between the data and the metadata.</summary>
    public string DescribeOuter()
    {
        var sb = new StringBuilder();
        EnsureInner();
        const int block = 0x10000;
        IMemoryReader reader = (IMemoryReader?)_xtsReader ?? new FileReader(_file, PfsOffset);
        long sbRel = SuperblockOffset - PfsOffset;
        byte[] head = new byte[block];
        reader.Read(sbRel, head, 0, block);
        sb.AppendLine($"outer superblock block {sbRel / block} of {PfsSize / block}:");
        Rows(sb, head.AsSpan(0, 0x5A0).ToArray());

        byte[] table = new byte[block];
        reader.Read(sbRel + block, table, 0, block);
        var ms = new MemoryStream(table);
        ProsperoPfsHeader hdr = ProsperoPfsHeader.ReadFromStream(new MemoryStream(head));
        for (int i = 0; i < hdr.DinodeCount; i++)
        {
            ProsperoDinodeS32 d = ProsperoDinodeS32.ReadFromStream(ms);
            sb.AppendLine($"inode {i}: mode 0x{(ushort)d.Mode:X4} nlink {d.Nlink} flags 0x{(uint)d.Flags:X} size 0x{d.Size:X} sizeC 0x{d.SizeCompressed:X} blocks {d.Blocks} time {d.Time1_sec}/{d.Time1_nsec} uid {d.Uid} gid {d.Gid} unk {d.Unk1:X}/{d.Unk2:X}");
            sb.AppendLine("   db: " + string.Join(" ", d.db.Select(x => x.block)));
            sb.AppendLine("   ib: " + string.Join(" ", d.ib.Select(x => x.block)));
            sb.AppendLine("   db0 sig " + Convert.ToHexString(d.db[0].sig)[..16] + " ib0 sig " + Convert.ToHexString(d.ib[0].sig)[..16]);
        }
        return sb.ToString();
    }

    /// <summary>Reads one block of the outer image as stored on disk (no decryption), for diagnostics.</summary>
    internal byte[] ReadOuterBlockRaw(long index)
    {
        byte[] data = new byte[0x10000];
        lock (_file) { _file.Position = PfsOffset + index * 0x10000; _file.ReadExactly(data); }
        return data;
    }
}

public sealed partial class PkgPackage
{
    /// <summary>Size of the stored inner image (pfs_image.dat) in the outer filesystem.</summary>
    internal long StoredImageSize
    {
        get { EnsureInner(); return _innerFile!.size; }
    }
}
