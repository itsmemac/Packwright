// Packwright.Pkg - decrypting reader for the outer image of packages built with a passcode.
//
// Packages built by Packwright (and by LibProsperoPKG) encrypt the outer image: every 64 KB block is one
// AES-XTS data unit. The key comes from the content id and the passcode, both public for debug packages.
// The block layout and sector numbering follow the LibProsperoPKG reader (GPL-3.0-or-later, SvenGDK):
// file-data blocks use the block index as the sector number, metadata blocks use the index with bit 47 set,
// and the block that holds the superblock is stored plain.
#nullable enable
using LibProsperoPkg.Util;

namespace Packwright.Pkg.Reader;

internal enum OuterBlockKind : byte
{
    /// <summary>File data: sector number is the block index.</summary>
    Data = 0,
    /// <summary>Filesystem metadata: sector number is the block index with bit 47 set.</summary>
    Signed = 1,
    /// <summary>The superblock: stored unencrypted.</summary>
    Plain = 2
}

/// <summary>Reads the outer image through block-wise AES-XTS decryption; only the blocks that are read are decrypted.</summary>
internal sealed class OuterXtsReader : IMemoryReader
{
    public const int BlockSize = 0x10000;
    private const ulong SignedFlag = 0x800000000000UL;
    private const int CacheSlots = 8;

    private readonly FileStream _file;
    private readonly long _baseOffset;
    private readonly XtsBlockTransform _xts;
    private readonly (long Block, OuterBlockKind Kind, byte[] Data)?[] _cache = new (long, OuterBlockKind, byte[])?[CacheSlots];
    private int _nextSlot;

    public OuterXtsReader(FileStream file, long baseOffset, byte[] tweakKey, byte[] dataKey, OuterBlockKind[] kinds)
    {
        _file = file;
        _baseOffset = baseOffset;
        _xts = new XtsBlockTransform(dataKey, tweakKey);
        Kinds = kinds;
    }

    /// <summary>How each block is stored. The array can be updated once the filesystem metadata has been read.</summary>
    public OuterBlockKind[] Kinds { get; }

    public void ClearCache() => Array.Clear(_cache);

    public void Read(long pos, byte[] buf, int offset, int count)
    {
        while (count > 0)
        {
            long block = pos / BlockSize;
            int inside = (int)(pos % BlockSize);
            int take = Math.Min(count, BlockSize - inside);
            Buffer.BlockCopy(GetBlock(block), inside, buf, offset, take);
            pos += take;
            offset += take;
            count -= take;
        }
    }

    private byte[] GetBlock(long block)
    {
        if (block < 0 || block >= Kinds.Length) throw new InvalidDataException("The outer image is read past its end.");
        OuterBlockKind kind = Kinds[block];
        lock (_xts)
        {
            foreach ((long Block, OuterBlockKind Kind, byte[] Data)? slot in _cache)
                if (slot is { } s && s.Block == block && s.Kind == kind)
                    return s.Data;

            byte[] data = new byte[BlockSize];
            lock (_file)
            {
                _file.Position = _baseOffset + block * BlockSize;
                _file.ReadExactly(data);
            }
            if (kind != OuterBlockKind.Plain)
                _xts.CryptSector(data, kind == OuterBlockKind.Signed ? SignedFlag | (ulong)block : (ulong)block, encrypt: false);

            _cache[_nextSlot] = (block, kind, data);
            _nextSlot = (_nextSlot + 1) % CacheSlots;
            return data;
        }
    }

    public void Dispose() => _xts.Dispose();
}
