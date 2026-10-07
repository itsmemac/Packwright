// Packwright.Pkg - optional console-compatibility key material, supplied by the user at run time.
//
// Packwright ships no keys. A package it builds is complete and readable without any (its inner and outer images use
// the public passcode scheme). A few fields of a package, though, are RSA-wrapped under public moduli or encrypted
// under a fixed key by the original tooling; a console may check them. The user can import that material from a file
// of their own. The file is checked against fingerprints (SHA-256 of each value, below) so a wrong or corrupt file is
// refused; the fingerprints do not reveal the values. Private keys are refused outright.
//
// File format: UTF-8 text, one "name=hex" per line, '#' starts a comment.
//   passcode0 .. passcode6   384-byte RSA-3072 public moduli, big-endian
//   mount-image              384-byte RSA-3072 public modulus, big-endian
//   metadata                 384-byte RSA-3072 public modulus, big-endian
//   naps-meta-xts            three 16-byte values (data key, tweak key, tweak), comma separated
#nullable enable
using System.Security.Cryptography;
using System.Text;

namespace Packwright.Pkg.Writer;

public sealed class NapsMetaKeys(byte[] dataKey, byte[] tweakKey, byte[] tweak)
{
    public byte[] DataKey { get; } = dataKey;
    public byte[] TweakKey { get; } = tweakKey;
    public byte[] Tweak { get; } = tweak;
}

/// <summary>The imported key material. Every part is optional; what is missing is simply not written.</summary>
public sealed class PkgKeySet
{
    public const int ModulusLength = 384;
    public const int PasscodeSlots = 7;

    private static readonly string[] PasscodeFingerprints =
    [
        "6c816fbf1e60a0e72a28cddeeba4dc4c3015bde2a87d550514614499b3ebb293",
        "ffe4d2e27f06baf7c988c538978f3cbd5d710be515015541d1f643329974578e",
        "92aec135409cccc0b8f7c3977cad2f0beb435dfb9187d31eefe6afb5ffd177db",
        "4a585ee47fea88d6b121d5e8afc9829438a27ee325cebec56a2ff537129a37c5",
        "541b30995c028fd89366c3b1c1dbde70a23fa2784eecfb3fd3dd732502c665fc",
        "54c7757e8142b538f6b0b1a2fbfd343a05c7158f2d5d5ce144934de5536cb175",
        "bcbe73d512d460c5b7002ceecc47fdf77d08567cb17cd08e3e05ca02fdc9038c",
    ];
    private const string MountImageFingerprint = "4510462812321cea26b4c0155e363955b16e31b89252b92510accad02eca5cd0";
    private const string MetadataFingerprint = "4a585ee47fea88d6b121d5e8afc9829438a27ee325cebec56a2ff537129a37c5";
    private const string NapsMetaFingerprint = "aa715bc167d14cddc44c9c408255f4f24c261483227270b7c203217433e77767";

    public static PkgKeySet None { get; } = new();

    /// <summary>The seven wrapping moduli for the entry keys, or null.</summary>
    public IReadOnlyList<byte[]>? PasscodeModuli { get; private set; }
    public byte[]? MountImageModulus { get; private set; }
    public byte[]? MetadataModulus { get; private set; }
    public NapsMetaKeys? NapsMeta { get; private set; }

    public bool IsEmpty => PasscodeModuli is null && MountImageModulus is null && MetadataModulus is null && NapsMeta is null;

    /// <summary>A line per part, saying what is present, for the log and the settings page.</summary>
    public string Describe() =>
        $"entry keys: {(PasscodeModuli is null ? "not available" : "imported")}; image key: {(MountImageModulus is null ? "not available" : "imported")}; " +
        $"header seal: {(MetadataModulus is null ? "not available" : "imported")}; install metadata: {(NapsMeta is null ? "not available" : "imported")}";

    /// <summary>Reads a key file. Throws <see cref="InvalidDataException"/> with a plain message when the file is not acceptable.</summary>
    public static PkgKeySet Load(string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This file contains a private key. Packwright only accepts public values and will not read it.");

        var set = new PkgKeySet();
        var passcode = new byte[PasscodeSlots][];
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) throw new InvalidDataException($"Unrecognised line in the key file: '{Shorten(line)}'.");
            string name = line[..eq].Trim().ToLowerInvariant();
            string value = line[(eq + 1)..].Trim();

            if (name.StartsWith("passcode", StringComparison.Ordinal) && int.TryParse(name.AsSpan(8), out int slot) && slot is >= 0 and < PasscodeSlots)
                passcode[slot] = Modulus(name, value, PasscodeFingerprints[slot]);
            else if (name == "mount-image")
                set.MountImageModulus = Modulus(name, value, MountImageFingerprint);
            else if (name == "metadata")
                set.MetadataModulus = Modulus(name, value, MetadataFingerprint);
            else if (name == "naps-meta-xts")
                set.NapsMeta = Xts(value);
            else
                throw new InvalidDataException($"Unknown entry '{name}' in the key file.");
        }
        if (passcode.All(m => m is not null)) set.PasscodeModuli = passcode;
        else if (passcode.Any(m => m is not null)) throw new InvalidDataException("The key file has only some of the seven passcode entries; it needs all of them or none.");
        return set;
    }

    private static byte[] Modulus(string name, string hex, string fingerprint)
    {
        byte[] value = FromHex(name, hex);
        if (value.Length != ModulusLength)
            throw new InvalidDataException($"'{name}' must be {ModulusLength} bytes; it is {value.Length}.");
        if (!Matches(value, fingerprint))
            throw new InvalidDataException($"'{name}' is not the value Packwright expects, so it was not imported.");
        return value;
    }

    private static NapsMetaKeys Xts(string value)
    {
        string[] parts = value.Split(',');
        if (parts.Length != 3) throw new InvalidDataException("'naps-meta-xts' needs three comma separated values.");
        byte[][] v = parts.Select(p => FromHex("naps-meta-xts", p.Trim())).ToArray();
        if (v.Any(b => b.Length != 16)) throw new InvalidDataException("Each 'naps-meta-xts' value must be 16 bytes.");
        if (!Matches([.. v[0], .. v[1], .. v[2]], NapsMetaFingerprint))
            throw new InvalidDataException("'naps-meta-xts' is not the value Packwright expects, so it was not imported.");
        return new NapsMetaKeys(v[0], v[1], v[2]);
    }

    private static bool Matches(byte[] value, string fingerprint) =>
        fingerprint.StartsWith('@') || Convert.ToHexString(SHA256.HashData(value)).Equals(fingerprint, StringComparison.OrdinalIgnoreCase);

    private static byte[] FromHex(string name, string hex)
    {
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { throw new InvalidDataException($"'{name}' is not valid hexadecimal."); }
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "...";
}
