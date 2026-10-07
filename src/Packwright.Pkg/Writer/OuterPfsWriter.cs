// Packwright.Pkg - writes the outer filesystem of a package: the stored inner image and its layout file wrapped in a
// signed, block-encrypted PFS.
//
// Layout ("data first"): the blocks of pfs_image.dat, the blocks of naps_pkg_layout.dat, then the superblock
// (stored plain), the inode table, the super-root directory, the inode_flat_path_table, the indirect blocks of the
// two files, and the uroot directory. Every block is hashed (SHA3-256 of the plain block) and the hashes are kept
// in the inodes; the superblock carries an integrity value over its own header. Blocks of pfs_image.dat are
// encrypted as AES-XTS data unit "block index"; every other block except the superblock uses the same index with
// bit 47 set. The structure follows the packages built by LibProsperoPKG (GPL-3.0-or-later, SvenGDK) and was
// checked block by block against real packages.
//
// The stored image is fed in as a stream of bytes (WriteImage) because its size is only known once the last block has
// been compressed; the layout file and the metadata blocks are written by Complete.
#nullable enable
using System.Buffers.Binary;
using LibProsperoPkg.PFS;
using Packwright.Pkg.Util;

namespace Packwright.Pkg.Writer;

/// <summary>What the container builder needs to know about the finished outer image.</summary>
public sealed class OuterImageInfo
{
    public required long PfsSize { get; init; }
    public required int BlockCount { get; init; }
    public required int SuperblockIndex { get; init; }
    public required int ImageBlocks { get; init; }
    public required int LayoutBlocks { get; init; }
    public required int InodeCount { get; init; }
    /// <summary>SHA3-256 of every plain block, 32 bytes each, in block order.</summary>
    public required byte[] BlockDigests { get; init; }
    /// <summary>CRC-32C of every block as stored (after encryption), in block order.</summary>
    public required uint[] BlockCrcs { get; init; }
    /// <summary>The plain superblock block.</summary>
    public required byte[] Superblock { get; init; }
    public required byte[] Seed { get; init; }
}

public sealed class OuterPfsWriter : IDisposable
{
    public const int BlockSize = 0x10000;
    private const int DirectBlocks = 12;
    private const int EntriesPerBlock = BlockSize / 36;
    private const ulong SignedFlag = 0x800000000000UL;

    private const ushort ModeDir = 0x416D;
    private const ushort ModeFile = 0x816D;
    private const uint FlagsInternal = 0x2000C;
    private const uint FlagsDir = 0x000C;
    private const uint FlagsFile = 0x000D;

    private const string ImageName = "pfs_image.dat";
    private const string LayoutName = "naps_pkg_layout.dat";

    private readonly Stream _output;
    private readonly long _start;
    private readonly byte[] _seed;
    private readonly long _timeSeconds;
    private readonly uint _timeNanoseconds;
    private readonly bool _encrypt;
    private readonly FastXts? _xts;
    private readonly byte[] _block = new byte[BlockSize];
    private byte[] _digests = new byte[1024 * 32];
    private uint[] _crcs = new uint[1024];
    private int _fill;
    private int _imageBlocks;
    private bool _imageFinished;

    /// <summary>Starts an outer image at the current position of <paramref name="output"/>, which must be seekable.</summary>
    public OuterPfsWriter(Stream output, byte[] ekpfs, byte[] seed, long timeSeconds, uint timeNanoseconds, bool encrypt)
    {
        if (seed.Length != 16) throw new ArgumentException("The seed must be 16 bytes.", nameof(seed));
        _output = output;
        _start = output.Position;
        _seed = seed;
        _timeSeconds = timeSeconds;
        _timeNanoseconds = timeNanoseconds;
        _encrypt = encrypt;
        if (encrypt) _xts = CreateXts(ekpfs, seed);
    }

    /// <summary>The AES-XTS keys of the outer image: (tweak, data).</summary>
    public static (byte[] Tweak, byte[] Data) DeriveKeys(byte[] ekpfs, byte[] seed)
    {
        var (tweak, data) = LibProsperoPkg.Util.Crypto.PfsGenEncKey(ekpfs, seed, newCrypt: true);
        return (tweak, data);
    }

    private static FastXts CreateXts(byte[] ekpfs, byte[] seed)
    {
        var (tweak, data) = DeriveKeys(ekpfs, seed);
        return new FastXts(data, tweak);
    }

    /// <summary>Bytes of the stored image written so far.</summary>
    public long ImageBytes => (long)_imageBlocks * BlockSize + _fill;

    /// <summary>Appends bytes to the stored image (pfs_image.dat).</summary>
    public void WriteImage(ReadOnlySpan<byte> data)
    {
        if (_imageFinished) throw new InvalidOperationException("The stored image is already complete.");
        while (!data.IsEmpty)
        {
            int take = Math.Min(BlockSize - _fill, data.Length);
            data[..take].CopyTo(_block.AsSpan(_fill));
            _fill += take;
            data = data[take..];
            if (_fill == BlockSize)
            {
                Emit(_imageBlocks++, _block, signed: false);
                _fill = 0;
            }
        }
    }

    /// <summary>Pads the stored image with zeros to a whole number of blocks.</summary>
    public long FinishImage()
    {
        if (_imageFinished) return (long)_imageBlocks * BlockSize;
        if (_fill > 0)
        {
            Array.Clear(_block, _fill, BlockSize - _fill);
            Emit(_imageBlocks++, _block, signed: false);
            _fill = 0;
        }
        if (_imageBlocks == 0) throw new InvalidOperationException("The stored image is empty.");
        _imageFinished = true;
        return (long)_imageBlocks * BlockSize;
    }

    private void Emit(int index, byte[] plain, bool signed, bool isSuperblock = false)
    {
        while ((index + 1) * 32L > _digests.Length)
        {
            Array.Resize(ref _digests, _digests.Length * 2);
            Array.Resize(ref _crcs, _crcs.Length * 2);
        }
        Sha3.Hash(plain, _digests.AsSpan(index * 32, 32));
        byte[] data = plain;
        if (_encrypt && !isSuperblock)
        {
            data = (byte[])plain.Clone();
            _xts!.Crypt(data, signed ? SignedFlag | (uint)index : (uint)index, encrypt: true);
        }
        _crcs[index] = Crc32C.Compute(data);
        _output.Position = _start + (long)index * BlockSize;
        _output.Write(data, 0, BlockSize);
    }

    /// <summary>Finishes the image: writes the layout file and all metadata blocks.</summary>
    /// <param name="layout">The layout file (naps_pkg_layout.dat).</param>
    /// <param name="logicalSize">Size of the expanded inner image (recorded as the compressed size of pfs_image.dat).</param>
    public OuterImageInfo Complete(byte[] layout, long logicalSize)
    {
        long storedSize = FinishImage();
        int imageBlocks = _imageBlocks;
        int layoutBlocks = (layout.Length + BlockSize - 1) / BlockSize;
        int dataBlocks = imageBlocks + layoutBlocks;
        int sbIndex = dataBlocks;
        int tableIndex = sbIndex + 1;
        int rootDirIndex = sbIndex + 2;
        int fltIndex = sbIndex + 3;

        var files = new[]
        {
            new FileLayout(ImageName, 0, imageBlocks, storedSize, logicalSize),
            new FileLayout(LayoutName, imageBlocks, layoutBlocks, layout.Length, layout.Length),
        };
        int next = fltIndex + 1;
        foreach (FileLayout f in files) next = f.PlanIndirect(next);
        int urootIndex = next;
        int total = urootIndex + 1;

        // ---- the layout file ----------------------------------------------------------------------------
        byte[] block = new byte[BlockSize];
        for (int i = 0; i < layoutBlocks; i++)
        {
            Array.Clear(block);
            int take = Math.Min(BlockSize, layout.Length - i * BlockSize);
            Buffer.BlockCopy(layout, i * BlockSize, block, 0, take);
            Emit(imageBlocks + i, block, signed: true);
        }

        // ---- structural blocks (plain content first: the inodes hash them) ---------------------------------------
        byte[] rootDir = DirentBlock((1, ProsperoDirentType.File, "inode_flat_path_table"), (2, ProsperoDirentType.Directory, "uroot"));
        byte[] flt = FltBlock(files.Select(f => f.Name).ToArray());
        byte[] urootDir = DirentBlock((2, ProsperoDirentType.Dot, "."), (2, ProsperoDirentType.DotDot, ".."),
            (3, ProsperoDirentType.File, ImageName), (4, ProsperoDirentType.File, LayoutName));

        // indirect blocks need the digests of the data blocks they point to
        foreach (FileLayout f in files) f.BuildIndirect(_digests, out f.IndirectBlocks);

        byte[] Dig(int index) => _digests.AsSpan(index * 32, 32).ToArray();
        byte[] Hash(byte[] b) => Sha3.Hash(b);

        var inodes = new List<ProsperoDinodeS32>
        {
            MetaInode(ModeDir, 1, FlagsInternal, BlockSize, _timeSeconds, _timeNanoseconds, rootDirIndex, Hash(rootDir)),
            MetaInode(ModeFile, 1, FlagsInternal, 0x40 + files.Length * 16, _timeSeconds, _timeNanoseconds, fltIndex, Hash(flt)),
            MetaInode(ModeDir, 3, FlagsDir, BlockSize, _timeSeconds, _timeNanoseconds, urootIndex, Hash(urootDir)),
        };
        foreach (FileLayout f in files)
        {
            var di = new ProsperoDinodeS32
            {
                Mode = (ProsperoInodeMode)ModeFile, Nlink = 1, Flags = (ProsperoInodeFlags)FlagsFile,
                Size = f.Size, SizeCompressed = f.Logical, Blocks = (uint)f.Count,
            };
            Stamp(di, _timeSeconds, _timeNanoseconds);
            for (int j = 0; j < Math.Min(f.Count, DirectBlocks); j++)
            {
                di.db[j].sig = Dig(f.First + j);
                di.db[j].block = f.First + j;
            }
            for (int j = 0; j < f.IndirectSlots.Count; j++)
            {
                di.ib[j].sig = f.IndirectSlots[j].Sig;
                di.ib[j].block = f.IndirectSlots[j].Block;
            }
            inodes.Add(di);
        }

        byte[] table = new byte[BlockSize];
        using (var ms = new MemoryStream(table))
            foreach (ProsperoDinodeS32 di in inodes) di.WriteToStream(ms);

        var sb = new byte[BlockSize];
        var root = new ProsperoDinodeS64 { Mode = 0, Nlink = 1, Flags = 0, Size = BlockSize, SizeCompressed = BlockSize, Blocks = 1 };
        root.Time1_sec = root.Time2_sec = root.Time3_sec = root.Time4_sec = _timeSeconds;
        root.Time1_nsec = root.Time2_nsec = root.Time3_nsec = root.Time4_nsec = _timeNanoseconds;
        root.db[0].sig = Hash(table);
        root.db[0].block = tableIndex;
        var header = new ProsperoPfsHeader
        {
            Version = ProsperoPfsHeader.VersionPs5, ReadOnly = 1, Mode = (ProsperoPfsMode)0x0D, BlockSize = BlockSize, NBlock = 1,
            DinodeCount = inodes.Count, Ndblock = total, DinodeBlockCount = 1, UnknownIndex = 1, Seed = _seed, InodeBlockSig = root,
        };
        using (var ms = new MemoryStream(sb)) header.WriteToStream(ms);
        byte[] icv = ComputeIcv(sb);
        icv.CopyTo(sb, 0x380);

        Emit(sbIndex, sb, signed: false, isSuperblock: true);
        Emit(tableIndex, table, signed: true);
        Emit(rootDirIndex, rootDir, signed: true);
        Emit(fltIndex, flt, signed: true);
        foreach (FileLayout f in files)
            foreach ((int index, byte[] data) in f.IndirectBlocks)
                Emit(index, data, signed: true);
        Emit(urootIndex, urootDir, signed: true);

        Array.Resize(ref _digests, total * 32);
        Array.Resize(ref _crcs, total);
        return new OuterImageInfo
        {
            PfsSize = (long)total * BlockSize, BlockCount = total, SuperblockIndex = sbIndex, ImageBlocks = imageBlocks,
            LayoutBlocks = layoutBlocks, InodeCount = inodes.Count, BlockDigests = _digests, BlockCrcs = _crcs, Superblock = sb, Seed = _seed,
        };
    }

    public void Dispose() => _xts?.Dispose();

    /// <summary>The integrity value of a superblock: SHA3-256 of its first 0x5A0 bytes with the value's own field zeroed.</summary>
    public static byte[] ComputeIcv(ReadOnlySpan<byte> superblock)
    {
        Span<byte> region = stackalloc byte[0x5A0];
        superblock[..0x5A0].CopyTo(region);
        region.Slice(0x380, 32).Clear();
        return Sha3.Hash(region);
    }

    private static void Stamp(ProsperoInode di, long seconds, uint nanoseconds)
    {
        di.Time1_sec = di.Time2_sec = di.Time3_sec = di.Time4_sec = seconds;
        di.Time1_nsec = di.Time2_nsec = di.Time3_nsec = di.Time4_nsec = nanoseconds;
    }

    private static ProsperoDinodeS32 MetaInode(ushort mode, ushort nlink, uint flags, long size, long seconds, uint nanoseconds, int block, byte[] hash)
    {
        var di = new ProsperoDinodeS32
        {
            Mode = (ProsperoInodeMode)mode, Nlink = nlink, Flags = (ProsperoInodeFlags)flags, Size = size, SizeCompressed = size, Blocks = 1,
        };
        Stamp(di, seconds, nanoseconds);
        di.db[0].sig = hash;
        di.db[0].block = block;
        return di;
    }

    private static byte[] DirentBlock(params (uint Inode, ProsperoDirentType Type, string Name)[] entries)
    {
        byte[] block = new byte[BlockSize];
        using var ms = new MemoryStream(block);
        foreach (var (inode, type, name) in entries)
            new ProsperoPfsDirent { InodeNumber = inode, Type = type, Name = name }.WriteToStream(ms);
        return block;
    }

    // The \x7FFLT table of the outer filesystem: a hash and a packed inode/ordinal per file.
    private static byte[] FltBlock(string[] names)
    {
        byte[] b = new byte[BlockSize];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 0x40);
        b[0x20] = 0x7F; b[0x21] = (byte)'F'; b[0x22] = (byte)'L'; b[0x23] = (byte)'T';
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x2C), (uint)names.Length);
        FlatPathTable.HeaderSeed.CopyTo(b.AsSpan(0x30));
        for (int i = 0; i < names.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x40 + i * 16), FlatPathTable.HashPath(names[i]));
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x48 + i * 16), (ulong)(3 + i) | ((ulong)i << 40));
        }
        return b;
    }

    // One file of the outer filesystem and the indirect blocks that address it.
    private sealed class FileLayout(string name, int first, int count, long size, long logical)
    {
        public string Name { get; } = name;
        public int First { get; } = first;
        public int Count { get; } = count;
        public long Size { get; } = size;
        public long Logical { get; } = logical;

        private int _single = -1, _top = -1, _second = -1, _secondCount;
        public List<(int Block, byte[] Sig)> IndirectSlots { get; } = [];
        public List<(int Index, byte[] Data)> IndirectBlocks = [];

        /// <summary>Reserves the indirect blocks from <paramref name="next"/>; returns the next free block.</summary>
        public int PlanIndirect(int next)
        {
            int extra = Count - DirectBlocks;
            if (extra <= 0) return next;
            _single = next++;
            int rest = extra - EntriesPerBlock;
            if (rest > 0)
            {
                _top = next++;
                _secondCount = (rest + EntriesPerBlock - 1) / EntriesPerBlock;
                if (_secondCount > EntriesPerBlock) throw new NotSupportedException("This image is too large for one outer file.");
                _second = next;
                next += _secondCount;
            }
            return next;
        }

        /// <summary>Builds the indirect blocks (and the slots the inode stores for them) from the data-block digests.</summary>
        public void BuildIndirect(byte[] digests, out List<(int Index, byte[] Data)> blocks)
        {
            blocks = [];
            if (_single < 0) return;

            byte[] Entries(int firstData, int n)
            {
                byte[] b = new byte[BlockSize];
                for (int k = 0; k < n; k++)
                {
                    digests.AsSpan((First + firstData + k) * 32, 32).CopyTo(b.AsSpan(k * 36));
                    BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(k * 36 + 32), First + firstData + k);
                }
                return b;
            }

            int extra = Count - DirectBlocks;
            byte[] single = Entries(DirectBlocks, Math.Min(extra, EntriesPerBlock));
            blocks.Add((_single, single));
            IndirectSlots.Add((_single, Sha3.Hash(single)));
            if (_top < 0) return;

            int done = DirectBlocks + EntriesPerBlock;
            byte[] top = new byte[BlockSize];
            for (int s = 0; s < _secondCount; s++)
            {
                int n = Math.Min(EntriesPerBlock, Count - done);
                byte[] second = Entries(done, n);
                done += n;
                int index = _second + s;
                blocks.Add((index, second));
                Sha3.Hash(second, top.AsSpan(s * 36, 32));
                BinaryPrimitives.WriteInt32LittleEndian(top.AsSpan(s * 36 + 32), index);
            }
            blocks.Add((_top, top));
            IndirectSlots.Add((_top, Sha3.Hash(top)));
        }
    }
}
