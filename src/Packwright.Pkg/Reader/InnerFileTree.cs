// Packwright.Pkg - file tree of the inner PFS image of a PS5 package.
//
// The PS5 inner inode layout (size at 0x08, logical offset at 0x60) follows LibProsperoPKG by SvenGDK
// (GPL-3.0-or-later), see ProsperoPs5InnerImageReader.ReadFileTree. This version reads only the
// metadata blocks it needs from a streaming image instead of an in-memory copy.
#nullable enable
using System.Buffers.Binary;
using LibProsperoPkg.PFS;

namespace Packwright.Pkg.Reader;

/// <summary>A regular file inside a package's inner image.</summary>
public sealed record PkgFile(string Path, long Size, long LogicalOffset)
{
    internal int Inode { get; init; }
}

internal static class InnerFileTree
{
    private const int InodeSize = 0xA8;

    public static IReadOnlyList<PkgFile> Read(Stream mount, long superblockOffset) => ReadAll(mount, superblockOffset).Files;

    /// <summary>The files and every directory (including empty ones) below the user root.</summary>
    public static (IReadOnlyList<PkgFile> Files, IReadOnlyList<string> Directories) ReadAll(Stream mount, long superblockOffset)
    {
        byte[] sb = new byte[0x40];
        ReadAt(mount, superblockOffset, sb);
        if (BinaryPrimitives.ReadInt64LittleEndian(sb) != 2 || BinaryPrimitives.ReadInt64LittleEndian(sb.AsSpan(8)) != 20130315)
            throw new InvalidDataException("The inner image does not start with a PFS superblock.");

        int blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x20));
        long inodeCount = BinaryPrimitives.ReadInt64LittleEndian(sb.AsSpan(0x30));
        if (blockSize <= 0 || inodeCount <= 0 || inodeCount > 1_000_000)
            throw new InvalidDataException("The inner image has an implausible superblock (block size or inode count).");

        // Inode records never straddle a block: each block holds blockSize / 0xA8 of them, then padding.
        int perBlock = blockSize / InodeSize;
        long tableBlocks = (inodeCount + perBlock - 1) / perBlock;
        byte[] table = new byte[tableBlocks * blockSize];
        ReadAt(mount, superblockOffset + blockSize, table);
        var nodes = new Inode[inodeCount];
        for (int i = 0; i < nodes.Length; i++)
        {
            ReadOnlySpan<byte> e = table.AsSpan((i / perBlock) * blockSize + (i % perBlock) * InodeSize, InodeSize);
            nodes[i] = new Inode(BinaryPrimitives.ReadUInt16LittleEndian(e),
                BinaryPrimitives.ReadInt64LittleEndian(e[8..]), BinaryPrimitives.ReadInt64LittleEndian(e[0x60..]));
        }

        var files = new List<PkgFile>();
        var directories = new List<string>();
        Walk(mount, nodes, 0, string.Empty, false, files, directories, new HashSet<uint>());
        return (files, directories);
    }

    private readonly record struct Inode(ushort Mode, long Size, long LogicalOffset)
    {
        public bool IsDirectory => (Mode & 0x4000) != 0;
    }

    // Walks from the super-root; only content at or below "uroot" is real package content.
    private static void Walk(Stream mount, Inode[] nodes, uint dirInode, string path, bool underUroot,
        List<PkgFile> files, List<string> directories, HashSet<uint> seen)
    {
        if (dirInode >= nodes.Length || !seen.Add(dirInode)) return;
        Inode dir = nodes[dirInode];
        if (!dir.IsDirectory || dir.Size <= 0 || dir.Size > 64 * 1024 * 1024) return;

        byte[] listing = new byte[dir.Size];
        ReadAt(mount, dir.LogicalOffset, listing);
        using var ms = new MemoryStream(listing, writable: false);
        while (ms.Position + 0x10 <= ms.Length)
        {
            long entryPos = ms.Position;
            ProsperoPfsDirent d;
            try { d = ProsperoPfsDirent.ReadFromStream(ms); }
            catch (Exception) { break; }
            if (d.EntSize <= 0) break;
            ms.Position = entryPos + d.EntSize;

            string name = d.Name;
            if (string.IsNullOrEmpty(name) || name == "." || name == ".." || d.InodeNumber >= nodes.Length) continue;

            bool childUnderUroot = underUroot || name == "uroot";
            string childPath = path.Length == 0 ? (name == "uroot" ? string.Empty : name) : path + "/" + name;
            if (d.Type == ProsperoDirentType.Directory)
            {
                if (childUnderUroot && childPath.Length > 0) directories.Add(childPath);
                Walk(mount, nodes, d.InodeNumber, childPath, childUnderUroot, files, directories, seen);
            }
            else if (d.Type == ProsperoDirentType.File && underUroot && childPath.Length > 0)
                files.Add(new PkgFile(childPath, nodes[d.InodeNumber].Size, nodes[d.InodeNumber].LogicalOffset) { Inode = (int)d.InodeNumber });
        }
    }

    /// <summary>The raw inode record, for diagnostics.</summary>
    internal static byte[] ReadInodeRaw(Stream mount, long superblockOffset, int inode)
    {
        byte[] sb = new byte[0x40];
        ReadAt(mount, superblockOffset, sb);
        int blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x20));
        int perBlock = blockSize / InodeSize;
        byte[] raw = new byte[InodeSize];
        ReadAt(mount, superblockOffset + blockSize + (long)(inode / perBlock) * blockSize + (long)(inode % perBlock) * InodeSize, raw);
        return raw;
    }

    private static void ReadAt(Stream s, long offset, byte[] buffer)
    {
        s.Position = offset;
        s.ReadExactly(buffer);
    }
}
