using Packwright.Core.Builders;
using Packwright.Pkg.Reader;

namespace Packwright.Core.Services;

public readonly record struct Ps5PackageExtractProgress(long CompletedBytes, long TotalBytes, string CurrentPath);
public sealed class Ps5PackageExtractResult
{
    public required string Destination { get; init; }
    public required int FileCount { get; init; }
    public required long ExtractedBytes { get; init; }
}

public static class Ps5PackageExtraction
{
    /// <summary>
    /// Extracts the application filesystem of a package to <paramref name="destination"/>, which must not exist.
    /// The passcode is only needed for the protected metadata entries; the files themselves are not encrypted.
    /// </summary>
    public static Task<Ps5PackageExtractResult> ExtractAsync(string packagePath, string destination,
        string passcode = Ps5DebugPackageCredentials.DefaultPasscode,
        IProgress<Ps5PackageExtractProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            string output = Path.GetFullPath(destination);
            if (Directory.Exists(output)) throw new IOException("The extraction destination already exists: " + output);

            using PkgPackage package = PkgPackage.Open(packagePath, passcode);
            IReadOnlyList<PkgFile> files;
            try { files = package.Files; }
            catch (PkgNotSupportedException ex) { throw new InvalidDataException(ex.Message, ex); }
            if (package.IsMetadataOnly || files.Count == 0)
                throw new InvalidDataException("The package contains no files that can be extracted.");

            IProgress<PkgExtractProgress>? forward = progress is null
                ? null
                : new Progress<PkgExtractProgress>(p => progress.Report(new Ps5PackageExtractProgress(p.CompletedBytes, p.TotalBytes, p.CurrentPath)));
            package.ExtractTo(output, forward, cancellationToken, includeMetadata: true);
            return new Ps5PackageExtractResult
            {
                Destination = output,
                FileCount = files.Count,
                ExtractedBytes = files.Sum(file => file.Size)
            };
        }, cancellationToken);
}
