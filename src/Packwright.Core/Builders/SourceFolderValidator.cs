namespace Packwright.Core.Builders;

/// <summary>Decides which files of a source folder or image are build artefacts rather than part of the title.</summary>
public static class SourceFolderValidator
{
    /// <summary>
    /// True for files a package must not carry: publishing project descriptors (<c>.gp4</c>, <c>.gp5</c>) and the
    /// <c>sce_suppl</c> folder, which a package build generates itself. Everything else is packed as it is.
    /// </summary>
    public static bool IsPublishingArtifact(string relativePath)
    {
        string path = relativePath.Replace('\\', '/').TrimStart('/');
        if (path.StartsWith("sce_suppl/", StringComparison.OrdinalIgnoreCase)) return true;
        string extension = Path.GetExtension(path);
        return extension.Equals(".gp4", StringComparison.OrdinalIgnoreCase) || extension.Equals(".gp5", StringComparison.OrdinalIgnoreCase);
    }
}
