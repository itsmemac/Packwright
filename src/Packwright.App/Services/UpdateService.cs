using System.Reflection;
using Packwright.Core.Services;
using Packwright.Infrastructure;

namespace Packwright.App.Services;

public enum UpdateState { UpToDate, Available, NoReleases, Failed }

public sealed record UpdateResult(UpdateState State, ReleaseInfo? Release, string Message);

/// <summary>
/// Remembers whether a newer Packwright is available. A check happens only when the user asks, or once a day when
/// they allowed automatic checks; it talks to GitHub and nowhere else.
/// </summary>
public static class UpdateService
{
    public static string CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>The newer release found by the last check, or null.</summary>
    public static ReleaseInfo? Available { get; private set; }

    public static event Action? Changed;

    /// <summary>True when a newer release is known and the user has not chosen to skip it.</summary>
    public static bool HasVisibleUpdate =>
        Available is { } release &&
        !string.Equals(AppServices.Instance.Settings.SkippedUpdateVersion, release.Version, StringComparison.OrdinalIgnoreCase);

    public static bool ShouldCheckOnStartup()
    {
        AppSettings settings = AppServices.Instance.Settings;
        return settings.CheckForUpdates && DateTime.UtcNow - settings.LastUpdateCheckUtc > TimeSpan.FromHours(20);
    }

    public static async Task<UpdateResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        AppServices services = AppServices.Instance;
        try
        {
            ReleaseInfo? latest = await UpdateChecker.GetLatestAsync(cancellationToken).ConfigureAwait(false);
            services.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
            services.SaveSettings();
            if (latest is null)
            {
                Available = null;
                Changed?.Invoke();
                return new UpdateResult(UpdateState.NoReleases, null, "No release has been published yet.");
            }
            if (!UpdateChecker.IsNewer(latest.Version, CurrentVersion))
            {
                Available = null;
                Changed?.Invoke();
                return new UpdateResult(UpdateState.UpToDate, latest, $"You have the latest version ({CurrentVersion}).");
            }
            Available = latest;
            Logger.Info($"Update available: {latest.Version} (you have {CurrentVersion}).");
            Changed?.Invoke();
            return new UpdateResult(UpdateState.Available, latest, $"Version {latest.Version} is available (you have {CurrentVersion}).");
        }
        catch (OnlineLookupException ex)
        {
            Logger.Warn("Update check: " + ex.Message);
            return new UpdateResult(UpdateState.Failed, null, ex.Message);
        }
    }

    public static void Skip(ReleaseInfo release)
    {
        AppServices.Instance.Settings.SkippedUpdateVersion = release.Version;
        AppServices.Instance.SaveSettings();
        Changed?.Invoke();
    }
}
