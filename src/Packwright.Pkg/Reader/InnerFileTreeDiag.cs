namespace Packwright.Pkg.Reader;

/// <summary>Diagnostics for the test project.</summary>
public static class InnerFileTreeDiag
{
    internal static byte[] Raw(Stream mount, long superblock, int inode) => InnerFileTree.ReadInodeRaw(mount, superblock, inode);
}
