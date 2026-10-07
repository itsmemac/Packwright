// Packwright.Pkg - plans the inner filesystem of a package from a list of files.
//
// Given the files (paths, sizes and whether each is an executable module) this works out everything about the
// inner image except the file contents: the inode and "afid" numbering, where each file sits in the expanded image,
// where the filesystem metadata goes, and the metadata itself (superblock, inode table, directory blocks, the two
// flat path tables and the afid table).
//
// The rules follow the LibProsperoPKG assembler (GPL-3.0-or-later, SvenGDK) and were checked against the metadata of
// real packages: the planner reproduces their metadata byte for byte from just their file lists.
#nullable enable
using System.Buffers.Binary;
using LibProsperoPkg.PFS;

namespace Packwright.Pkg.Writer;

/// <summary>One file to place in the inner image. <paramref name="Path"/> is relative to the user root, with '/' separators.</summary>
public sealed record InnerFile(string Path, long Size, bool IsExecutable, bool IsHole = false)
{
    /// <summary>A reserved slot in the data region that holds no file (the original tool leaves one in some packages).</summary>
    public static InnerFile Hole(long size) => new(string.Empty, size, false, true);
}

public sealed class PlannedFile
{
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required uint Inode { get; init; }
    public required uint Afid { get; init; }
    public required long LogicalOffset { get; init; }
    public required bool IsExecutable { get; init; }
}

public sealed class InnerImagePlan
{
    /// <summary>The files in the order their data is laid out (afid order). Data is packed back to back from offset 0.</summary>
    public required IReadOnlyList<PlannedFile> Files { get; init; }
    /// <summary>End of the packed file data.</summary>
    public required long DataEnd { get; init; }
    /// <summary>Offset of the superblock in the expanded image; the metadata runs from here to <see cref="MountSize"/>.</summary>
    public required long MetadataOffset { get; init; }
    /// <summary>The filesystem metadata, from the superblock block to the end of the image.</summary>
    public required byte[] Metadata { get; init; }
    /// <summary>Size of the expanded inner image.</summary>
    public required long MountSize { get; init; }
    /// <summary>Directories and files below the user root (what the header of a finalised image counts).</summary>
    public required int ContentInodes { get; init; }
    /// <summary>Files outside sce_sys.</summary>
    public required int AppFileCount { get; init; }
}

public static class InnerImagePlanner
{
    public const int BlockSize = 0x10000;
    private const int InodeSize = 0xA8;
    private const int InodesPerBlock = BlockSize / InodeSize;
    private const long ReserveBlocks = 61;

    private sealed class Dir
    {
        public string Name = "", FullPath = "";
        public Dir? Parent;
        public readonly List<Dir> SubDirs = [];
        public readonly List<File> Files = [];
        public uint Inode;
        public int DirentOffset = -1;
        public readonly List<ProsperoPfsDirent> Dirents = [];
    }

    private sealed class File
    {
        public string Name = "", FullPath = "";
        public long Size;
        public bool IsExecutable;
        public bool IsHole;
        public Dir Parent = null!;
        public uint Inode, Afid;
        public long LogicalOffset;
        public int DirentOffset = -1;
        public bool InSceSys => FullPath.StartsWith("/sce_sys/", StringComparison.Ordinal);
    }

    private sealed class Node
    {
        public uint Inode;
        public bool IsDirectory;
        public ushort Mode, Nlink = 1;
        public uint Flags;
        public long Size;
        public ulong LogicalOffset;
        public uint Afid;
        public int Parent = -1, DirentOffset = -1;
    }

    public static InnerImagePlan Plan(IReadOnlyList<InnerFile> input, IEnumerable<string>? directories = null,
        long timeSeconds = 0, uint timeNanoseconds = 0)
    {
        if (input.Count == 0) throw new ArgumentException("The inner image needs at least one file.", nameof(input));
        var dataOrder = new List<File>();
        Dir uroot = BuildTree(input, directories ?? [], dataOrder);

        // Inodes: 0 super-root, 1-3 the internal tables, 4 the user root, then directories in pre-order, then regular
        // files directory by directory in post-order (children before their parent), names in ordinal order.
        var dirsPre = new List<Dir>();
        CollectPre(uroot, dirsPre);
        uint next = 4;
        foreach (Dir d in dirsPre) d.Inode = next++;
        var dirsPost = new List<Dir>();
        CollectPost(uroot, dirsPost);
        var filesByInode = new List<File>();
        foreach (Dir d in dirsPost)
            foreach (File f in d.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                f.Inode = next++;
                filesByInode.Add(f);
            }
        int inodeCount = (int)next;

        // Afids number the files in data order, which is simply the order the caller listed them in (the order
        // does not affect correctness, only where each file sits). Data is packed back to back.
        var afidOrder = dataOrder;
        long cursor = 0;
        for (int i = 0; i < afidOrder.Count; i++)
        {
            afidOrder[i].Afid = (uint)i;
            afidOrder[i].LogicalOffset = cursor;
            cursor += afidOrder[i].Size;
        }
        long dataEnd = cursor;

        BuildDirents(uroot);

        // The two flat path tables and the afid table.
        var inodeTable = new List<(ulong Hash, ulong Packed)>();
        var aprTable = new List<(ulong Hash, ulong Packed)>();
        foreach (Dir d in dirsPre)
            if (d != uroot)
                inodeTable.Add((FlatPathTable.HashPath(d.FullPath), PackInode(d.Inode, true, !IsSceSysDir(d), 0)));
        foreach (File f in filesByInode)
        {
            bool apr = !f.InSceSys;
            inodeTable.Add((FlatPathTable.HashPath(f.FullPath), PackInode(f.Inode, false, apr, f.Afid)));
            if (apr) aprTable.Add((FlatPathTable.HashPath(f.FullPath), ((ulong)f.Size & 0xFFFFFFFFFFUL) | ((ulong)f.Afid << 40)));
        }
        byte[] inodeTableBytes = WriteTable(inodeTable);
        byte[] aprTableBytes = WriteTable(aprTable);

        // afid_to_ino_table: the number of afids plus two, then the inode of every afid (-1 for a hole), then two -1.
        var afidTable = new List<int> { afidOrder.Count + 2 };
        afidTable.AddRange(afidOrder.Select(f => f.IsHole ? -1 : (int)f.Inode));
        afidTable.Add(-1);
        afidTable.Add(-1);
        byte[] afidBytes = new byte[afidTable.Count * 4];
        for (int i = 0; i < afidTable.Count; i++) BinaryPrimitives.WriteInt32LittleEndian(afidBytes.AsSpan(i * 4), afidTable[i]);

        // The blocks that follow the superblock and inode table: the super-root listing, the three tables, every
        // directory listing and a trailing empty block. Anything larger than one block takes as many as it needs.
        byte[] superRoot = Pad(Serialize(
        [
            new ProsperoPfsDirent { InodeNumber = 1, Name = "inode_flat_path_table", Type = ProsperoDirentType.File },
            new ProsperoPfsDirent { InodeNumber = 2, Name = "apr_flat_path_table", Type = ProsperoDirentType.File },
            new ProsperoPfsDirent { InodeNumber = 3, Name = "afid_to_ino_table", Type = ProsperoDirentType.File },
            new ProsperoPfsDirent { InodeNumber = uroot.Inode, Name = "uroot", Type = ProsperoDirentType.Directory },
        ]));
        var dirBlocks = dirsPre.Select(d => Pad(Serialize(d.Dirents))).ToList();
        var contents = new List<byte[]> { superRoot, Pad(inodeTableBytes), Pad(aprTableBytes), Pad(afidBytes) };
        contents.AddRange(dirBlocks);
        contents.Add(new byte[BlockSize]); // trailing empty block

        // Geometry: the data region, a fixed reserve of blocks, then the metadata.
        long dataBlocks = (dataEnd + BlockSize - 1) / BlockSize;
        while ((dataBlocks + ReserveBlocks) % 4 != 0) dataBlocks++;
        long superblockBlock = dataBlocks + ReserveBlocks;
        int inodeBlocks = (inodeCount + InodesPerBlock - 1) / InodesPerBlock;
        long firstContentBlock = superblockBlock + 1 + inodeBlocks;
        long totalBlocks = firstContentBlock + contents.Sum(c => c.Length / BlockSize);

        // Inode records.
        var nodes = new List<Node>();
        long block = firstContentBlock;
        ulong Take(byte[] content)
        {
            ulong at = (ulong)(block * BlockSize);
            block += content.Length / BlockSize;
            return at;
        }
        nodes.Add(new Node { Inode = 0, IsDirectory = true, Mode = 0x416D, Flags = 0x20010, Size = superRoot.Length, LogicalOffset = Take(superRoot) });
        nodes.Add(new Node { Inode = 1, Mode = 0x816D, Flags = 0x20010, Size = inodeTableBytes.Length, LogicalOffset = Take(contents[1]) });
        nodes.Add(new Node { Inode = 2, Mode = 0x816D, Flags = 0x20010, Size = aprTableBytes.Length, LogicalOffset = Take(contents[2]) });
        nodes.Add(new Node { Inode = 3, Mode = 0x816D, Flags = 0x20010, Size = afidBytes.Length, LogicalOffset = Take(contents[3]) });
        for (int i = 0; i < dirsPre.Count; i++)
        {
            Dir d = dirsPre[i];
            bool root = d == uroot;
            bool system = IsSceSysDir(d);
            nodes.Add(new Node
            {
                Inode = d.Inode, IsDirectory = true, Mode = (ushort)(system ? 0x4168 : 0x416D),
                Nlink = (ushort)(2 + d.SubDirs.Count + (root ? 1 : 0)), Flags = system ? 0x20010u : 0x10u,
                Size = dirBlocks[i].Length, LogicalOffset = Take(dirBlocks[i]),
                Parent = root ? -1 : (int)d.Parent!.Inode, DirentOffset = root ? -1 : d.DirentOffset,
            });
        }
        foreach (File f in filesByInode)
            nodes.Add(new Node
            {
                Inode = f.Inode, Mode = (ushort)(f.InSceSys ? 0x8168 : 0x816D),
                Flags = 0x10u | (f.IsExecutable ? 0x40u : 0x20u) | (f.InSceSys ? 0x20000u : 0u),
                Size = f.Size, LogicalOffset = (ulong)f.LogicalOffset, Afid = f.Afid,
                Parent = (int)f.Parent.Inode, DirentOffset = f.DirentOffset,
            });

        // Assemble the metadata region: superblock, inode table, then the content blocks.
        byte[] metadata = new byte[(totalBlocks - superblockBlock) * BlockSize];
        WriteSuperblock(metadata, inodeCount, totalBlocks, superblockBlock, inodeBlocks, timeSeconds, timeNanoseconds);
        foreach (Node n in nodes)
        {
            int offset = BlockSize + (int)(n.Inode / InodesPerBlock) * BlockSize + (int)(n.Inode % InodesPerBlock) * InodeSize;
            WriteInode(metadata.AsSpan(offset, InodeSize), n, timeSeconds, timeNanoseconds);
        }
        long position = (1 + inodeBlocks) * (long)BlockSize;
        foreach (byte[] content in contents)
        {
            content.CopyTo(metadata, position);
            position += content.Length;
        }

        return new InnerImagePlan
        {
            Files = afidOrder.Where(f => !f.IsHole).Select(f => new PlannedFile
            {
                Path = f.FullPath[1..], Size = f.Size, Inode = f.Inode, Afid = f.Afid,
                LogicalOffset = f.LogicalOffset, IsExecutable = f.IsExecutable,
            }).ToList(),
            DataEnd = dataEnd,
            MetadataOffset = superblockBlock * BlockSize,
            Metadata = metadata,
            MountSize = totalBlocks * BlockSize,
            ContentInodes = nodes.Count(n => n.Parent >= 0),
            AppFileCount = afidOrder.Count(f => !f.IsHole && !f.InSceSys),
        };
    }

    // ---- tree ------------------------------------------------------------------------------------------

    private static Dir BuildTree(IReadOnlyList<InnerFile> input, IEnumerable<string> directories, List<File> dataOrder)
    {
        var uroot = new Dir();
        var lookup = new Dictionary<string, Dir>(StringComparer.Ordinal) { [""] = uroot };
        Dir GetDir(string path)
        {
            if (lookup.TryGetValue(path, out Dir? existing)) return existing;
            int slash = path.LastIndexOf('/');
            Dir parent = GetDir(slash < 0 ? "" : path[..slash]);
            var dir = new Dir { Name = path[(slash + 1)..], FullPath = "/" + path, Parent = parent };
            parent.SubDirs.Add(dir);
            lookup[path] = dir;
            return dir;
        }
        foreach (string d in directories) GetDir(d.Trim('/'));
        foreach (InnerFile f in input)
        {
            if (f.IsHole)
            {
                dataOrder.Add(new File { Size = f.Size, IsHole = true, Parent = uroot });
                continue;
            }
            string path = f.Path.TrimStart('/');
            int slash = path.LastIndexOf('/');
            Dir parent = GetDir(slash < 0 ? "" : path[..slash]);
            var node = new File
            {
                Name = path[(slash + 1)..], FullPath = "/" + path, Size = f.Size, IsExecutable = f.IsExecutable, Parent = parent,
            };
            parent.Files.Add(node);
            dataOrder.Add(node);
        }
        return uroot;
    }

    private static void CollectPre(Dir dir, List<Dir> list)
    {
        list.Add(dir);
        foreach (Dir d in dir.SubDirs.OrderBy(d => d.Name, StringComparer.Ordinal)) CollectPre(d, list);
    }

    private static void CollectPost(Dir dir, List<Dir> list)
    {
        foreach (Dir d in dir.SubDirs.OrderBy(d => d.Name, StringComparer.Ordinal)) CollectPost(d, list);
        list.Add(dir);
    }

    private static bool IsSceSysDir(Dir d) => d.FullPath == "/sce_sys" || d.FullPath.StartsWith("/sce_sys/", StringComparison.Ordinal);

    // ---- directories -----------------------------------------------------------------------------------

    private static void BuildDirents(Dir dir)
    {
        dir.Dirents.Clear();
        dir.Dirents.Add(new ProsperoPfsDirent { Name = ".", InodeNumber = dir.Inode, Type = ProsperoDirentType.Dot });
        dir.Dirents.Add(new ProsperoPfsDirent { Name = "..", InodeNumber = dir.Parent?.Inode ?? dir.Inode, Type = ProsperoDirentType.DotDot });
        foreach (Dir d in dir.SubDirs.OrderBy(d => d.Name, StringComparer.Ordinal))
            dir.Dirents.Add(new ProsperoPfsDirent { Name = d.Name, InodeNumber = d.Inode, Type = ProsperoDirentType.Directory });
        foreach (File f in dir.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
            dir.Dirents.Add(new ProsperoPfsDirent { Name = f.Name, InodeNumber = f.Inode, Type = ProsperoDirentType.File });

        // An entry never reaches a block boundary: when the next one would touch it, the previous entry is stretched to
        // fill the rest of its block and the next entry starts the following block.
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        int offset = 0, previousOffset = 0;
        ProsperoPfsDirent? previous = null;
        foreach (ProsperoPfsDirent e in dir.Dirents)
        {
            int boundary = (offset / BlockSize + 1) * BlockSize;
            if (offset % BlockSize != 0 && offset + e.EntSize >= boundary)
            {
                if (previous is not null) previous.EntSize = boundary - previousOffset;
                offset = boundary;
            }
            offsets[e.Name] = offset;
            previous = e;
            previousOffset = offset;
            offset += e.EntSize;
        }
        foreach (Dir d in dir.SubDirs) d.DirentOffset = offsets[d.Name];
        foreach (File f in dir.Files) f.DirentOffset = offsets[f.Name];
        foreach (Dir d in dir.SubDirs.OrderBy(d => d.Name, StringComparer.Ordinal)) BuildDirents(d);
    }

    // Writes the entries of a listing one after another (earlier entries were already stretched to the block ends).
    private static byte[] Serialize(IEnumerable<ProsperoPfsDirent> dirents)
    {
        using var ms = new MemoryStream();
        foreach (ProsperoPfsDirent d in dirents) d.WriteToStream(ms);
        return ms.ToArray();
    }

    // Pads to a whole number of blocks (at least one).
    private static byte[] Pad(byte[] data)
    {
        int blocks = Math.Max(1, (data.Length + BlockSize - 1) / BlockSize);
        byte[] padded = new byte[blocks * BlockSize];
        data.CopyTo(padded, 0);
        return padded;
    }

    // ---- tables and records ------------------------------------------------------------------------------

    private static ulong PackInode(uint inode, bool directory, bool apr, uint afid)
    {
        ulong packed = inode & 0xFFFFFFUL;
        if (directory) packed |= 0x40000000UL;
        if (!apr) packed |= 0x80000000UL;
        return packed | ((directory ? 0xFFFFFFUL : afid) << 40);
    }

    private static byte[] WriteTable(List<(ulong Hash, ulong Packed)> entries)
    {
        entries.Sort((a, b) => a.Hash.CompareTo(b.Hash));
        byte[] table = new byte[FlatPathTable.HeaderSize + entries.Count * 16];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 1);
        table[0x04] = 0x10;
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0x08), 0x40);
        table[0x20] = 0x7F; table[0x21] = (byte)'F'; table[0x22] = (byte)'L'; table[0x23] = (byte)'T';
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0x2C), (uint)entries.Count);
        FlatPathTable.HeaderSeed.CopyTo(table.AsSpan(0x30));
        for (int i = 0; i < entries.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(FlatPathTable.HeaderSize + i * 16), entries[i].Hash);
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(FlatPathTable.HeaderSize + i * 16 + 8), entries[i].Packed);
        }
        return table;
    }

    // The inode record of the inner filesystem. Unlike a classic PFS inode, the 8 bytes at 0x60 hold the file's
    // logical offset (or the directory block), and the next three words hold the file number, the parent inode and
    // the entry's offset inside its parent's directory listing.
    private static void WriteInode(Span<byte> dst, Node n, long seconds, uint nanoseconds)
    {
        dst.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(dst, n.Mode);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[2..], n.Nlink);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], n.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(dst[8..], n.Size);
        BinaryPrimitives.WriteInt64LittleEndian(dst[0x10..], n.Size);
        for (int t = 0; t < 4; t++) BinaryPrimitives.WriteInt64LittleEndian(dst[(0x18 + t * 8)..], seconds);
        for (int t = 0; t < 4; t++) BinaryPrimitives.WriteUInt32LittleEndian(dst[(0x38 + t * 4)..], nanoseconds);
        BinaryPrimitives.WriteUInt64LittleEndian(dst[0x60..], n.LogicalOffset);
        int fileNumber = n.Parent < 0 || n.IsDirectory ? -1 : (int)n.Afid;
        BinaryPrimitives.WriteInt32LittleEndian(dst[0x68..], fileNumber);
        BinaryPrimitives.WriteInt32LittleEndian(dst[0x6C..], n.Parent);
        BinaryPrimitives.WriteInt32LittleEndian(dst[0x70..], n.DirentOffset);
    }

    private static void WriteSuperblock(byte[] block, int inodeCount, long totalBlocks, long superblockBlock, int inodeBlocks,
        long seconds, uint nanoseconds)
    {
        Span<byte> sb = block;
        BinaryPrimitives.WriteInt64LittleEndian(sb, 2);
        BinaryPrimitives.WriteInt64LittleEndian(sb[8..], 20130315);
        sb[0x1A] = 1;                                                          // read-only
        BinaryPrimitives.WriteUInt16LittleEndian(sb[0x1C..], 0x18);            // mode
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x20..], BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(sb[0x28..], 1);
        BinaryPrimitives.WriteInt64LittleEndian(sb[0x30..], inodeCount);
        BinaryPrimitives.WriteInt64LittleEndian(sb[0x38..], totalBlocks);
        BinaryPrimitives.WriteInt64LittleEndian(sb[0x40..], inodeBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x50..], BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x54..], 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x58..], (uint)(inodeBlocks * BlockSize)); // size of the inode table
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x60..], (uint)(inodeBlocks * BlockSize));
        for (int t = 0; t < 4; t++) BinaryPrimitives.WriteInt64LittleEndian(sb[(0x68 + t * 8)..], seconds);
        for (int t = 0; t < 4; t++) BinaryPrimitives.WriteUInt32LittleEndian(sb[(0x88 + t * 4)..], nanoseconds);
        BinaryPrimitives.WriteInt64LittleEndian(sb[0xB0..], inodeBlocks);
        BinaryPrimitives.WriteInt64LittleEndian(sb[0xD8..], superblockBlock + 1); // first inode block
        // The inode table is described by direct block pointers (40 bytes each: a 32-byte signature, then the block).
        // The first points at the table; every further block of a longer table is marked with -1.
        if (inodeBlocks > 12)
            throw new NotSupportedException("Packages with more than about 4,600 files need indirect inode blocks, which are not supported yet.");
        for (int k = 1; k < inodeBlocks; k++)
            BinaryPrimitives.WriteInt64LittleEndian(sb[(0xD8 + 0x28 * k)..], -1);
        sb[0x368] = 1;
    }
}
