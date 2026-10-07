using System.Text.Json.Nodes;
using Packwright.Core.Builders;
using Packwright.Pkg.Reader;
using Packwright.Pkg.Writer;

namespace Packwright.Core.Services;

/// <summary>
/// Edits a debug package's details (param.json, artwork) without rebuilding it: the inner image is copied as it is and
/// only the metadata container is written again. This works while the content id stays the same.
/// </summary>
public static class Ps5PackageMetadataEditor
{
    /// <summary>
    /// Writes <paramref name="targetPath"/> from <paramref name="sourcePath"/> with a patched param.json and, when given, a new
    /// icon (the PNG and its texture). Returns false, and writes nothing, when the package needs a full rebuild instead (the
    /// patch changes the content id, the package is not a readable debug package).
    /// </summary>
    public static Task<bool> TryEditAsync(string sourcePath, string targetPath, Func<byte[], byte[]> patchParam, byte[]? iconPng,
        string passcode, IProgress<Ps5DebugPackageProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            string source = Path.GetFullPath(sourcePath);
            string target = Path.GetFullPath(targetPath);
            string staging = target + ".packwright-new";
            bool inPlace = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);

            using (PkgPackage package = PkgPackage.Open(source, passcode))
            {
                if (package.IsMetadataOnly || package.IsRetail || package.IsPatchImage) return false;
                PkgEntry? paramEntry = package.Entries.FirstOrDefault(entry => entry.Id == PkgEntryTable.ParamJson);
                if (paramEntry is null) return false;

                byte[] param = patchParam(package.ReadEntry(paramEntry));
                string? contentId = ParseContentId(param);
                if (contentId is not null && !contentId.Equals(package.ContentId, StringComparison.Ordinal)) return false;

                var replacements = new Dictionary<uint, byte[]> { [PkgEntryTable.ParamJson] = param };
                if (iconPng is not null)
                {
                    replacements[0x1200] = iconPng;
                    try { replacements[0x1280] = Ps5DdsEncoder.EncodePng(iconPng); }
                    catch (Exception ex) when (ex is SixLabors.ImageSharp.ImageFormatException or NotSupportedException or ArgumentException)
                    {
                        throw new InvalidDataException("The icon is not a picture Packwright can read.", ex);
                    }
                }

                var relay = progress is null ? null : new Progress<PkgBuildProgress>(value =>
                    progress.Report(new Ps5DebugPackageProgress(value.Stage, value.Done, value.Total, value.Item)));
                try
                {
                    PkgRepackager.ReplaceEntries(package, staging, replacements, Ps5BuildKeys.Current, passcode, null, relay, cancellationToken);
                }
                catch
                {
                    try { if (File.Exists(staging)) File.Delete(staging); } catch (IOException) { }
                    throw;
                }
            }

            // the source is closed now: swap the new file in
            try
            {
                if (inPlace) File.Move(staging, target, overwrite: true);
                else
                {
                    if (File.Exists(target)) throw new IOException("The output already exists: " + target);
                    File.Move(staging, target);
                }
            }
            catch
            {
                try { if (File.Exists(staging)) File.Delete(staging); } catch (IOException) { }
                throw;
            }
            return true;
        }, cancellationToken);

    private static string? ParseContentId(byte[] param)
    {
        try { return JsonNode.Parse(param)?["contentId"]?.GetValue<string>(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { return null; }
    }
}
