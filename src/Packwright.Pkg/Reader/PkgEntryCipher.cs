// Packwright.Pkg - decryption of protected metadata entries (title data, license, trophy bindings).
//
// A protected entry is AES-128-CBC encrypted. The key and IV come from a digest of the entry's own table
// row plus a key seed that is derived from the content id and the passcode (both public for debug packages).
// The scheme follows the LibProsperoPKG 1.2 reader (GPL-3.0-or-later, SvenGDK). No publisher key is involved.
#nullable enable
using System.Buffers.Binary;
using System.Security.Cryptography;
using LibProsperoPkg.Util;

namespace Packwright.Pkg.Reader;

internal static class PkgEntryCipher
{
    public const string DefaultPasscode = "00000000000000000000000000000000";

    /// <summary>
    /// Decrypts a protected entry. Packages built with the publisher profile hash with SHA3-256 and the rest
    /// with SHA-256; the entry's known magic (where it has one) picks the profile, otherwise
    /// <paramref name="preferPublisherProfile"/> decides.
    /// </summary>
    public static byte[] Decrypt(byte[] stored, PkgEntry entry, string contentId, string passcode, bool preferPublisherProfile)
    {
        if (passcode.Length != 32) throw new PkgNotSupportedException("The passcode must be 32 characters.");
        if (contentId.Length != 36) throw new InvalidDataException("The package has no usable content id to derive the entry key from.");
        if (stored.Length < 16 || stored.Length % 16 != 0) throw new InvalidDataException($"Entry 0x{entry.Id:X4} has an invalid stored length.");

        uint keyIndex = (entry.Flags2 >> 12) & 0xF;
        byte[] row = MetaRow(entry);
        foreach (bool publisher in new[] { preferPublisherProfile, !preferPublisherProfile })
        {
            byte[] keySeed = Crypto.ComputeKeys(contentId, passcode, keyIndex, publisher);
            byte[] data = [.. row, .. keySeed];
            byte[] digest = publisher ? Crypto.Sha3_256(data) : Crypto.Sha256(data);

            byte[] plain = new byte[stored.Length];
            using Aes aes = Aes.Create();
            aes.Key = digest[16..32];
            int written = aes.DecryptCbc(stored, digest[..16], plain, PaddingMode.None);
            if (written != plain.Length) continue;

            if (KnownMagic(entry.Id, plain) is { } known)
            {
                if (known) return plain[..(int)entry.Size];
                continue;
            }
            return plain[..(int)entry.Size];
        }
        throw new InvalidDataException(
            $"Entry 0x{entry.Id:X4} could not be decrypted with this passcode (or it uses a protection scheme this reader does not support).");
    }

    // The 32-byte table row the key is bound to: six big-endian fields, then 8 zero bytes.
    private static byte[] MetaRow(PkgEntry e)
    {
        byte[] row = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(0), e.Id);
        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(4), e.NameTableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(8), e.Flags1);
        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(12), e.Flags2);
        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(16), checked((uint)e.Offset));
        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(20), checked((uint)e.Size));
        return row;
    }

    // true/false when the entry has a known leading magic, null when it has none.
    private static bool? KnownMagic(uint id, byte[] plain) => id switch
    {
        0x0402 => plain.Length >= 4 && plain[0] == (byte)'N' && plain[1] == (byte)'P' && plain[2] == (byte)'T' && plain[3] == (byte)'D',
        0x0403 or 0x2020 or 0x2021 => plain.Length >= 4 && plain[0] == 0xD2 && plain[1] == 0x94 && plain[2] == 0xA0 && plain[3] == 0x18,
        _ => null
    };
}
