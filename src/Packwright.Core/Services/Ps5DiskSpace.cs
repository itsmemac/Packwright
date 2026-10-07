using System.Globalization;

namespace Packwright.Core.Services;

/// <summary>Free-space preflight outcome.</summary>
public enum Ps5DiskSpaceStatus
{
    Ok,
    NearLimit,
    Insufficient
}

/// <summary>Result of a free-space preflight, with a human-readable description.</summary>
public readonly record struct Ps5DiskSpaceCheck(Ps5DiskSpaceStatus Status, string Message);

/// <summary>
/// Free-space preflight for building a package. The builder streams file data straight into the output, so the only
/// space it needs is the package itself (the inner image is stored as it is, plus a small amount of metadata).
/// </summary>
public static class Ps5DiskSpace
{
    private const long Megabyte = 1024L * 1024;
    private const int SafetyPercent = 12;
    private const int WarnPercent = 25;
    private const long Headroom = 512 * Megabyte;

    private sealed record Requirement(string Root, long Bytes, string What);

    /// <summary>Estimates and checks free space for a package build.</summary>
    /// <param name="rawPayloadBytes">Source payload upper bound (dump total, or image file length).</param>
    /// <param name="outputPath">Destination package path.</param>
    /// <param name="tempDirectory">Not used by the builder; kept so callers do not need to change.</param>
    public static Ps5DiskSpaceCheck Check(long rawPayloadBytes, string outputPath, string? tempDirectory) =>
        Evaluate([new Requirement(RootOf(outputPath), PackageSize(rawPayloadBytes), "output package")]);

    /// <summary>
    /// Free-space preflight for an extract-then-build: the extracted staging tree, measured against the volume it is
    /// written to, plus the output package.
    /// </summary>
    public static Ps5DiskSpaceCheck CheckStagedImage(long rawPayloadBytes, long stagingBytes,
        string outputPath, string? tempDirectory)
    {
        string output = RootOf(outputPath);
        string staging = RootOf(tempDirectory ?? Path.GetTempPath());
        var requirements = new List<Requirement>();
        if (string.Equals(output, staging, StringComparison.OrdinalIgnoreCase))
            requirements.Add(new Requirement(output, PackageSize(rawPayloadBytes) + stagingBytes, "output package + staging tree"));
        else
        {
            requirements.Add(new Requirement(output, PackageSize(rawPayloadBytes), "output package"));
            if (stagingBytes > 0) requirements.Add(new Requirement(staging, stagingBytes, "staging tree"));
        }
        return Evaluate(requirements);
    }

    /// <summary>True when the exception is an out-of-disk-space failure.</summary>
    public static bool IsInsufficient(Exception exception) =>
        exception is IOException io && (io.HResult & 0xFFFF) is 112 or 39;   // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL

    public static string Describe(Exception exception) =>
        IsInsufficient(exception) ? "there is not enough free space on the destination drive" : exception.Message;

    private static long PackageSize(long payload) => Math.Max(0, payload) + Math.Max(16 * Megabyte, payload / 50) + 64 * Megabyte;

    private static Ps5DiskSpaceCheck Evaluate(IReadOnlyList<Requirement> requirements)
    {
        Requirement? worstRequirement = null;
        Ps5DiskSpaceStatus worst = Ps5DiskSpaceStatus.Ok;
        long worstFree = 0, worstNeed = 0;
        foreach (Requirement requirement in requirements)
        {
            long need = WithMargin(requirement.Bytes, SafetyPercent);
            long free = Free(requirement.Root);
            Ps5DiskSpaceStatus status = free < need ? Ps5DiskSpaceStatus.Insufficient
                : free < WithMargin(requirement.Bytes, WarnPercent) ? Ps5DiskSpaceStatus.NearLimit : Ps5DiskSpaceStatus.Ok;
            if (worstRequirement is null || status > worst)
            {
                worst = status; worstRequirement = requirement; worstFree = free; worstNeed = need;
            }
        }
        if (worstRequirement is null) return new Ps5DiskSpaceCheck(Ps5DiskSpaceStatus.Ok, "free space not checked");
        string message = worst switch
        {
            Ps5DiskSpaceStatus.Insufficient => $"not enough free space: need ~{Gb(worstNeed)} GB on {worstRequirement.Root} ({worstRequirement.What}), have {Gb(worstFree)} GB free",
            Ps5DiskSpaceStatus.NearLimit => $"low free space on {worstRequirement.Root} ({worstRequirement.What}): need ~{Gb(worstNeed)} GB, have {Gb(worstFree)} GB free",
            _ => $"free space OK on {worstRequirement.Root} ({worstRequirement.What}): have {Gb(worstFree)} GB",
        };
        return new Ps5DiskSpaceCheck(worst, message);
    }

    private static long WithMargin(long bytes, int percent)
    {
        try { return checked((long)Math.Ceiling(bytes * (1.0 + percent / 100.0)) + Headroom); }
        catch (OverflowException) { return long.MaxValue; }
    }

    private static long Free(string root)
    {
        try { return new DriveInfo(root).AvailableFreeSpace; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return long.MaxValue; }
    }

    private static string RootOf(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? path; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private static string Gb(long bytes) => (bytes / 1073741824.0).ToString("N1", CultureInfo.InvariantCulture);
}
