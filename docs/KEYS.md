# Compatibility keys (optional)

Packwright ships **no keys** of any kind. A package it builds is complete and readable without any: the inner image and
the outer image use the public passcode scheme (the passcode is `00000000000000000000000000000000` unless you choose
another), and Packwright reads, verifies and extracts its own packages with no key at all.

A few fields of a package are normally sealed by the original tools with public RSA values (the moduli of the public
keys), or encrypted under a fixed key. A console may check them. If you have those values you can import them, and
Packwright uses them when it builds a package. Without them those fields are filled with random bytes of the right
length, which is fine for reading the package on a PC and may or may not be accepted by a console.

You are responsible for where the values come from. Packwright never downloads, extracts or generates them, and it
never accepts a private key: a file that contains one is refused.

## Importing

Settings > Compatibility keys > **Import key file...** and pick the file. Packwright checks it, keeps a copy in its data
folder (`keys.txt`) and uses it for every package it builds. **Remove** deletes the copy.

## File format

UTF-8 text, one `name=value` per line, `#` starts a comment. Values are hexadecimal.

| Name | Value | Used for |
| --- | --- | --- |
| `passcode0` ... `passcode6` | seven 384-byte RSA-3072 public moduli, big-endian | the wrapped keys in the container's entry-key table |
| `mount-image` | a 384-byte RSA-3072 public modulus, big-endian | the wrapped image key |
| `metadata` | a 384-byte RSA-3072 public modulus, big-endian | the seal over the container header |
| `naps-meta-xts` | three 16-byte values (data key, tweak key, tweak), comma separated | the install metadata table (`naps_meta_18.dat`); not written by this version |

Every value is checked against a fingerprint (its SHA-256) that is part of Packwright's source. The fingerprints do not
reveal the values; they only tell Packwright whether the file you picked is the expected one. A wrong or corrupt value
is refused with a message, and nothing is imported.

The seven `passcode` lines have to be given together or not at all. Each line is optional otherwise: what is missing is
filled with random bytes.
