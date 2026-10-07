// Packwright.Pkg - builds the metadata container (the "CNT" part of a package): the header, the system entries
// (entry keys, image key, general digests, entry table, digest table, name table) and the data entries (parameters,
// artwork, protected title data, play-go files, the per-block image digests).
//
// All fields are big-endian. The layout and the digest rules follow the packages built by LibProsperoPKG
// (GPL-3.0-or-later, SvenGDK) and were checked field by field against real packages. The parts that are RSA-wrapped
// or sealed by the original tooling need public values the user imports (see PkgKeySet); without them those fields
// are filled with random bytes of the right length and the package is still complete and readable.
#nullable enable
using Packwright.Pkg.Util;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Packwright.Pkg.Writer;

/// <summary>One data entry of the container. Its bytes come from <see cref="Data"/> or, for big files, from <see cref="Open"/>.</summary>
public sealed class CntEntry
{
    public required uint Id { get; init; }
    public byte[]? Data { get; init; }
    public Func<Stream>? Open { get; init; }
    public long Length { get; init; }
    /// <summary>The name stored for the entry; by default the standard one for its id.</summary>
    public string? NameOverride { get; init; }
    public string? Name => NameOverride ?? PkgEntryTable.NameOf(Id);
    /// <summary>The flag words of the entry; by default the standard ones for its id.</summary>
    public uint? Flags1 { get; init; }
    public uint? Flags2 { get; init; }
}

public sealed class CntParameters
{
    public required string ContentId { get; init; }
    public required string Passcode { get; init; }
    public required PkgKeySet Keys { get; init; }
    public required IReadOnlyList<CntEntry> Entries { get; init; }
    /// <summary>Size of the outer image that sits between the finalised-image header and the container.</summary>
    public required long PfsSize { get; init; }
    /// <summary>The 16-byte seed stored in the outer superblock.</summary>
    public required byte[] ImageSeed { get; init; }
    /// <summary>SHA3-256 of the plain outer superblock block.</summary>
    public required byte[] SuperblockDigest { get; init; }
    /// <summary>SHA3-256 of the finalised-image header block.</summary>
    public required byte[] FihDigest { get; init; }
    /// <summary>True for a title whose parameter file asks for "upgradable" DRM.</summary>
    public bool Upgradable { get; init; }
}

public sealed class CntResult
{
    public required long Size { get; init; }
    public required int EntryCount { get; init; }
    public required long MandatorySize { get; init; }
}

public static class CntBuilder
{
    private const long BodyOffset = 0x2000;
    private const long Fih = 0x10000;
    private const uint VersionDate = 0x20240508, VersionHash = 0x090FBFC1;
    private const ulong PfsFlags = 0xA00000000000030C;
    private const ushort EntryKeysLength = 0xB80;
    private const int ImageKeyLength = 0x800;
    private const int GeneralDigestsLength = 0x1E0;
    private const int ScEntryCount = 6;

    private static readonly uint[] PlayGoIds = [0x1001, 0x2010, 0x2011, 0x3000];

    private sealed class Slot
    {
        public uint Id;
        public string? Name;
        public long Offset, Size;       // Size is the logical size
        public uint Flags1, Flags2;
        public CntEntry? Source;
    }

    /// <summary>Writes the container to <paramref name="output"/> starting at <paramref name="start"/>. The size is a multiple of 64 KB.</summary>
    public static CntResult Build(Stream output, long start, CntParameters p, CancellationToken cancellationToken = default)
    {
        string cid = p.ContentId;
        if (cid.Length != 36) throw new ArgumentException("The content id must be 36 characters.", nameof(p));
        byte[] ekpfs = LibProsperoPkg.Util.Crypto.ComputeKeys(cid, p.Passcode, 1, useSha3: true);

        // ---- entry list in container order ---------------------------------------------------------------
        var data = p.Entries.OrderBy(OrderKey).ThenBy(e => e.Id).ToList();
        int count = ScEntryCount + data.Count;
        var slots = new List<Slot>();
        void Add(uint id, long size, CntEntry? source = null) => slots.Add(new Slot
        {
            Id = id, Size = size, Source = source, Name = source?.Name,
            Flags1 = source?.Flags1 ?? PkgEntryTable.Flags1(id), Flags2 = source?.Flags2 ?? PkgEntryTable.Flags2(id),
        });
        Add(PkgEntryTable.EntryKeys, EntryKeysLength);
        Add(PkgEntryTable.ImageKey, ImageKeyLength);
        Add(PkgEntryTable.GeneralDigests, GeneralDigestsLength);
        Add(PkgEntryTable.Metas, count * 32L);
        Add(PkgEntryTable.Digests, count * 32L);
        slots.Add(new Slot { Id = PkgEntryTable.EntryNames, Flags1 = PkgEntryTable.Flags1(PkgEntryTable.EntryNames) });
        foreach (CntEntry e in data) Add(e.Id, e.Length, e);

        // names: registered in ordinal order; entries without a name use offset 0
        var nameOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        var nameBytes = new List<byte> { 0 };
        foreach (string name in slots.Select(s => s.Name).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (nameOffsets.ContainsKey(name)) continue;
            nameOffsets[name] = (uint)nameBytes.Count;
            nameBytes.AddRange(Encoding.ASCII.GetBytes(name));
            nameBytes.Add(0);
        }
        slots.First(s => s.Id == PkgEntryTable.EntryNames).Size = nameBytes.Count;

        long offset = BodyOffset;
        foreach (Slot s in slots)
        {
            s.Offset = offset;
            offset = Align(offset + s.Size, 16);
        }
        long bodySize = Align(offset, 0x10000) - BodyOffset;
        long containerSize = BodyOffset + bodySize;
        Slot imageDigests = slots.FirstOrDefault(s => s.Id == PkgEntryTable.ImageDigests)
            ?? throw new ArgumentException("The image digest entry is required.", nameof(p));
        Slot imageKey = slots.First(s => s.Id == PkgEntryTable.ImageKey);

        // ---- header ---------------------------------------------------------------------------------------------
        byte[] header = new byte[0x1000];
        long packageSize = Fih + p.PfsSize + containerSize;
        WriteHeader(header, p, slots, count, bodySize, containerSize, packageSize, imageDigests, imageKey);

        // ---- system entries (all but the digest table, which needs the others) ------------------------------------
        var body = new BodyWriter(output, start);
        body.Write(slots.First(s => s.Id == PkgEntryTable.EntryKeys).Offset, BuildEntryKeys(cid, p.Passcode, p.Keys));
        body.Write(imageKey.Offset, BuildImageKey(ekpfs, p.Keys));
        Slot metas = slots.First(s => s.Id == PkgEntryTable.Metas);
        byte[] metaTable = BuildMetas(slots, nameOffsets);
        body.Write(metas.Offset, metaTable);
        body.Write(slots.First(s => s.Id == PkgEntryTable.EntryNames).Offset, [.. nameBytes]);

        // ---- data entries ---------------------------------------------------------------------------------------------
        var storedDigests = new Dictionary<uint, byte[]>();    // SHA3 of the stored bytes, for the digest table
        var plainDigests = new Dictionary<uint, byte[]>();     // SHA3 of the plain bytes, for the general digests
        foreach (Slot s in slots.Where(s => s.Source is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CntEntry e = s.Source!;
            byte[] bytes = e.Data ?? ReadAll(e);
            if (bytes.Length != e.Length) throw new InvalidDataException($"Entry 0x{e.Id:X4} changed size while it was being written.");
            plainDigests[e.Id] = Sha3.Hash(bytes);
            if ((s.Flags1 & PkgEntryTable.Flags1Protected) != 0)
            {
                byte[] sealedBytes = Protect(bytes, s, string.IsNullOrEmpty(s.Name) ? 0u : nameOffsets[s.Name], cid, p.Passcode);
                body.Write(s.Offset, sealedBytes);
                storedDigests[e.Id] = Sha3.Hash(sealedBytes);
            }
            else
            {
                body.Write(s.Offset, bytes);
                storedDigests[e.Id] = plainDigests[e.Id];
            }
        }

        // ---- general digests (need the header fields and the plain entries) -------------------------------------
        byte[] general = BuildGeneralDigests(header, p, plainDigests);
        Slot generalSlot = slots.First(s => s.Id == PkgEntryTable.GeneralDigests);
        body.Write(generalSlot.Offset, general);

        // ---- digest table: SHA3-256 of the stored bytes of every entry but the table itself -----------------------
        var ordered = slots.OrderBy(s => s.Id).ToList();
        byte[] table = new byte[count * 32];
        for (int i = 0; i < ordered.Count; i++)
        {
            Slot s = ordered[i];
            if (s.Id == PkgEntryTable.Digests) continue;
            bool isProtected = (s.Flags1 & PkgEntryTable.Flags1Protected) != 0;
            long length = isProtected ? Align(s.Size, 16) : s.Size;
            byte[] hash = s.Source is not null && storedDigests.TryGetValue(s.Id, out byte[]? known) && !isProtected
                ? known
                : Sha3.Hash(body.Read(s.Offset, length));
            hash.CopyTo(table, i * 32);
        }
        Slot digestSlot = slots.First(s => s.Id == PkgEntryTable.Digests);
        body.Write(digestSlot.Offset, table);
        body.Pad(start + containerSize);

        // ---- header digests and seal -------------------------------------------------------------------------------
        ReadOnlySpan<byte> Region(Slot s, long size) => body.Read(s.Offset, size);
        byte[] sc1 = [.. Region(slots.First(s => s.Id == PkgEntryTable.EntryKeys), EntryKeysLength), .. Region(imageKey, ImageKeyLength),
            .. Region(generalSlot, GeneralDigestsLength), .. Region(metas, metas.Size), .. Region(digestSlot, digestSlot.Size)];
        byte[] sc2 = [.. Region(slots.First(s => s.Id == PkgEntryTable.EntryKeys), EntryKeysLength), .. Region(imageKey, ImageKeyLength),
            .. Region(generalSlot, GeneralDigestsLength), .. Region(metas, ScEntryCount * 32)];
        Sha3.Hash(sc1).CopyTo(header, 0x100);
        Sha3.Hash(sc2).CopyTo(header, 0x120);
        Sha3.Hash(table).CopyTo(header, 0x140);
        Sha3.Hash(body.ReadRange(start + BodyOffset, bodySize)).CopyTo(header, 0x160);

        byte[] descriptor = [.. Region(imageKey, ImageKeyLength)];
        Sha3.Hash(descriptor).CopyTo(header, 0x520);
        Sha3.Hash(body.Read(imageDigests.Offset, imageDigests.Size)).CopyTo(header, 0x540);

        Sha3.Hash(header.AsSpan(0, 0xFE0)).CopyTo(header, 0xFE0);
        byte[] seal = Wrap(p.Keys.MetadataModulus, Sha3.Hash(header));

        body.Write(0, header);
        body.Write(0x1000, seal);
        return new CntResult { Size = containerSize, EntryCount = count, MandatorySize = imageDigests.Offset };
    }

    // The order of the data entries in the container: parameters, protected entries, the two largest pictures, then the
    // per-block digests and play-go chunk, the remaining media, and the play-go tables last.
    private static int OrderKey(CntEntry e) => e.Id switch
    {
        PkgEntryTable.ParamJson => 0,
        _ when ((e.Flags1 ?? PkgEntryTable.Flags1(e.Id)) & PkgEntryTable.Flags1Protected) != 0 => 1,
        0x2040 or 0x2060 => 2,
        PkgEntryTable.ImageDigests => 3,
        PkgEntryTable.PlayGoChunk => 4,
        PkgEntryTable.PlayGoHashTable or PkgEntryTable.PlayGoFicm or 0x3000 => 6,
        _ => 5,
    };

    private static long Align(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    private static byte[] ReadAll(CntEntry e)
    {
        using Stream s = e.Open!();
        byte[] bytes = new byte[e.Length];
        s.ReadExactly(bytes);
        return bytes;
    }

    private static void WriteHeader(byte[] h, CntParameters p, List<Slot> slots, int count, long bodySize, long containerSize,
        long packageSize, Slot imageDigests, Slot imageKey)
    {
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(at), v);
        void U64(int at, ulong v) => BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(at), v);
        Slot metas = slots.First(s => s.Id == PkgEntryTable.Metas);

        h[0] = 0x7F; h[1] = (byte)'C'; h[2] = (byte)'N'; h[3] = (byte)'T';
        U32(0x04, 0x00020001);
        U32(0x08, 0x80000000);
        U32(0x0C, 0x0C);
        U32(0x10, (uint)count);
        BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(0x14), ScEntryCount);
        BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(0x16), (ushort)count);
        U32(0x18, (uint)metas.Offset);
        uint mainSize = (uint)(EntryKeysLength + ImageKeyLength + GeneralDigestsLength + 2 * count * 32);
        U32(0x1C, mainSize);
        U64(0x20, BodyOffset);
        U64(0x28, (ulong)bodySize);
        U64(0x30, (ulong)imageDigests.Offset);
        Encoding.ASCII.GetBytes(p.ContentId).CopyTo(h, 0x40);
        U32(0x70, p.Upgradable ? 0x10u : 0u);
        U32(0x74, 0x20);
        U32(0x78, 0x02020000u | (p.Upgradable ? 0x08000000u : 0u));
        U32(0x7C, (uint)containerSize);
        U32(0x80, VersionDate);
        U32(0x84, VersionHash);
        U32(0x9C, p.Upgradable ? 1u : 0u);
        Encoding.ASCII.GetBytes(p.ContentId).CopyTo(h, 0x200);

        U32(0x400, 1);
        U32(0x404, 1);
        U64(0x408, PfsFlags);
        U64(0x410, Fih);
        U64(0x418, (ulong)p.PfsSize);
        U64(0x428, (ulong)packageSize);
        U64(0x430, (ulong)packageSize);
        U32(0x438, 0x10000);
        p.SuperblockDigest.CopyTo(h, 0x440);
        p.FihDigest.CopyTo(h, 0x460);
        p.ImageSeed.CopyTo(h, 0x4A0);
        U64(0x4B0, (ulong)(Fih + p.PfsSize));
        U64(0x4B8, (ulong)containerSize);
        U32(0x510, (uint)imageKey.Offset);
        U32(0x514, ImageKeyLength);
        U32(0x518, (uint)imageDigests.Offset);
        U32(0x51C, (uint)imageDigests.Size);
    }

    private static byte[] BuildMetas(List<Slot> slots, Dictionary<string, uint> names)
    {
        var sorted = slots.OrderBy(s => s.Id).ToList();
        byte[] t = new byte[slots.Count * 32];
        for (int i = 0; i < sorted.Count; i++)
        {
            Slot s = sorted[i];
            Span<byte> r = t.AsSpan(i * 32, 32);
            BinaryPrimitives.WriteUInt32BigEndian(r, s.Id);
            BinaryPrimitives.WriteUInt32BigEndian(r[4..], string.IsNullOrEmpty(s.Name) ? 0 : names[s.Name]);
            BinaryPrimitives.WriteUInt32BigEndian(r[8..], s.Flags1);
            BinaryPrimitives.WriteUInt32BigEndian(r[12..], s.Flags2);
            BinaryPrimitives.WriteUInt32BigEndian(r[16..], (uint)s.Offset);
            BinaryPrimitives.WriteUInt32BigEndian(r[20..], (uint)s.Size);
        }
        return t;
    }

    // seed digest, seven key digests, seven wrapped keys (384 bytes each)
    private static byte[] BuildEntryKeys(string contentId, string passcode, PkgKeySet keys)
    {
        byte[] result = new byte[EntryKeysLength];
        Sha3.Hash(Encoding.ASCII.GetBytes(contentId.PadRight(48, '\0'))).CopyTo(result, 0);
        for (uint i = 0; i < 7; i++)
        {
            byte[] key = LibProsperoPkg.Util.Crypto.ComputeKeys(contentId, passcode, i, useSha3: true);
            byte[] digest = Sha3.Hash(key);
            for (int k = 0; k < 32; k++) digest[k] ^= key[k];
            digest.CopyTo(result, 32 + 32 * (int)i);
            byte[] wrapped = Wrap(keys.PasscodeModuli?[(int)i], i == 0 ? Encoding.ASCII.GetBytes(passcode) : key);
            wrapped.CopyTo(result, 256 + 384 * (int)i);
        }
        return result;
    }

    // the image key wrapped under the mount-image modulus, repeated to fill the entry
    private static byte[] BuildImageKey(byte[] ekpfs, PkgKeySet keys)
    {
        byte[] result = new byte[ImageKeyLength];
        for (int at = 0; at < ImageKeyLength; at += 384)
            Wrap(keys.MountImageModulus, ekpfs).AsSpan(0, Math.Min(384, ImageKeyLength - at)).CopyTo(result.AsSpan(at));
        return result;
    }

    // RSA with the public modulus and exponent 65537, PKCS#1 v1.5 type 2 padding; random bytes when the modulus was not imported
    private static byte[] Wrap(byte[]? modulus, byte[] data)
    {
        if (modulus is null) return RandomNumberGenerator.GetBytes(PkgKeySet.ModulusLength);
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = [0x01, 0x00, 0x01] });
        return rsa.Encrypt(data, RSAEncryptionPadding.Pkcs1);
    }

    // AES-128-CBC with a key and IV taken from SHA3-256 of the entry's table row and the passcode-derived key seed.
    // The plain bytes are zero padded to a whole number of blocks; the table keeps the logical size.
    private static byte[] Protect(byte[] plain, Slot slot, uint nameOffset, string contentId, string passcode)
    {
        byte[] row = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(row, slot.Id);
        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(4), nameOffset);
        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(8), slot.Flags1);
        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(12), slot.Flags2);
        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(16), (uint)slot.Offset);
        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(20), (uint)slot.Size);
        byte[] seed = LibProsperoPkg.Util.Crypto.ComputeKeys(contentId, passcode, (slot.Flags2 >> 12) & 0xF, useSha3: true);
        byte[] digest = Sha3.Hash([.. row, .. seed]);
        byte[] padded = new byte[Align(plain.Length, 16)];
        plain.CopyTo(padded, 0);
        using Aes aes = Aes.Create();
        aes.Key = digest[16..32];
        return aes.EncryptCbc(padded, digest[..16], PaddingMode.None);
    }

    // The general digests entry: a type word, a bit set of the digests present, then 14 slots of 32 bytes.
    private static byte[] BuildGeneralDigests(byte[] header, CntParameters p, Dictionary<uint, byte[]> plain)
    {
        byte[] game = p.SuperblockDigest;
        byte[] content = Sha3.Hash([.. header.AsSpan(0x40, 0x38), .. game, .. new byte[32]]);
        byte[] headerDigest = Sha3.Hash([.. header.AsSpan(0, 0x40), .. header.AsSpan(0x400, 0x80)]);
        byte[]? Concat(uint[] ids)
        {
            var parts = ids.Where(plain.ContainsKey).OrderBy(i => i).Select(i => plain[i]).ToList();
            return parts.Count == 0 ? null : Sha3.Hash(parts.SelectMany(b => b).ToArray());
        }

        byte[] result = new byte[GeneralDigestsLength];
        BinaryPrimitives.WriteUInt16BigEndian(result, 0xD256);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), 0x0102);
        uint set = 0;
        void Put(int slot, int bit, byte[]? value)
        {
            if (value is null) return;
            set |= 1u << bit;
            value.CopyTo(result, 0x20 + slot * 32);
        }
        Put(0, 1, content);
        Put(1, 2, game);
        Put(2, 3, headerDigest);
        Put(3, 4, Concat(plain.Keys.Where(Reader.PkgSystemMedia.Contains).ToArray()));
        Put(5, 6, plain.TryGetValue(PkgEntryTable.ParamJson, out byte[]? param) ? param : null);
        Put(6, 7, Concat(PlayGoIds));
        Put(11, 12, game);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0x1C), set);
        return result;
    }
    /// <summary>Random-access writer over the container region of the output stream.</summary>
    private sealed class BodyWriter(Stream output, long start)
    {
        public void Write(long offset, byte[] bytes) { output.Position = start + offset; output.Write(bytes, 0, bytes.Length); }
        public byte[] Read(long offset, long length) => ReadRange(start + offset, length);
        public byte[] ReadRange(long position, long length)
        {
            byte[] b = new byte[length];
            long saved = output.Position;
            output.Position = position;
            output.ReadExactly(b);
            output.Position = saved;
            return b;
        }
        public void Pad(long end)
        {
            if (output.Length < end) output.SetLength(end);
        }
    }
}
