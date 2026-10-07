using Packwright.Pkg.Reader;

namespace Packwright.Core.Services;

/// <summary>Outcome of opening a package with the canonical reader.</summary>
public sealed record PackageReaderVerificationResult(int FileCount, string? EbootPath)
{
    public bool HasEboot => EbootPath is not null;
}

/// <summary>
/// Opens a package with the one canonical reader (the same reader used to browse and extract
/// packages) so both builders are held to a single acceptance criterion.
/// </summary>
public static class PackageReaderVerification
{
    /// <summary>
    /// Reconstructs the inner filesystem index and reports the file count plus the path of
    /// <c>eboot.bin</c> when present. Throws when the package cannot be decoded at all.
    /// </summary>
    public static PackageReaderVerificationResult Inspect(string packagePath, string? passcode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        using PkgPackage package = PkgPackage.Open(packagePath, passcode);

        IReadOnlyList<PkgFile> files;
        try { files = package.Files; }
        catch (PkgNotSupportedException ex) { throw new InvalidDataException(ex.Message, ex); }

        string? ebootPath = null;
        foreach (PkgFile file in files)
        {
            string path = file.Path.Replace('\\', '/').TrimStart('/');
            if (path.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/eboot.bin", StringComparison.OrdinalIgnoreCase))
            {
                ebootPath = file.Path;
                break;
            }
        }
        return new PackageReaderVerificationResult(files.Count, ebootPath);
    }
}
