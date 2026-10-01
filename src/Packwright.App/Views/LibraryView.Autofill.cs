using System.Text;
using Avalonia.Interactivity;
using Packwright.App.Models;
using Packwright.App.Services;
using Packwright.Core.Models;
using Packwright.Core.Services;
using Packwright.Core.Tasks;
using Packwright.Infrastructure;

namespace Packwright.App.Views;

public partial class LibraryView
{
    private sealed record Proposal(LibraryItem Item, MetadataSuggestion Suggestion, byte[]? IconPng);

    /// <summary>
    /// Looks through the library for titles with missing details, works out what it can offline, shows what it
    /// found, and only then saves it (in the library, and optionally into the files themselves).
    /// </summary>
    private async Task FixMissingDetailsAsync()
    {
        Avalonia.Controls.Window? owner = Dialogs.OwnerOf(this);
        List<LibraryItem> targets = _items.Where(item => item.HasMissing).ToList();
        if (targets.Count == 0)
        {
            await Dialogs.ShowMessageAsync(owner, "Fix missing details", "No title in the library is missing details.");
            return;
        }

        var proposals = new List<Proposal>();
        int done = 0;
        foreach (LibraryItem item in targets)
        {
            Status($"Looking for missing details... {++done:N0} of {targets.Count:N0}");
            MetadataSuggestion suggestion;
            try { suggestion = await Task.Run(() => Ps5MetadataSuggester.Suggest(item.Game)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warn($"Could not look for details of {item.FileName}: {ex.Message}");
                continue;
            }
            Trim(suggestion, item);
            byte[]? icon = suggestion.Icon is null ? null : await IconTools.SquarePngAsync(suggestion.Icon);
            if (suggestion.Icon is not null && icon is null) suggestion.Icon = null;
            if (suggestion.HasAnything) proposals.Add(new Proposal(item, suggestion, icon));
        }

        if (proposals.Count == 0)
        {
            Status("Nothing more could be worked out.");
            await Dialogs.ShowMessageAsync(owner, "Fix missing details",
                $"{targets.Count:N0} title(s) are missing details, but nothing more could be worked out from the files " +
                "and their names. Their metadata was probably stripped when they were dumped.");
            return;
        }

        await ReviewSaveAndOfferWriteAsync(owner, "Fix missing details", proposals,
            $"{proposals.Count:N0} of {targets.Count:N0} title(s) with missing details can be improved.");
    }

    /// <summary>Shows what was found, saves it in the library on confirmation, then offers to write it into the files.</summary>
    private async Task ReviewSaveAndOfferWriteAsync(Avalonia.Controls.Window? owner, string title,
        List<Proposal> proposals, string header)
    {
        if (!await Dialogs.ConfirmTextAsync(owner, title, header + " Nothing changes until you save.",
                Review(proposals), "Save in library"))
        {
            Status("No changes made.");
            return;
        }

        foreach (Proposal proposal in proposals) SaveProposal(proposal);
        AutoSizeColumns();
        ApplyFilters();
        await ShowSelectionAsync();
        Status($"Saved details for {proposals.Count:N0} title(s) in the library.");

        // Writing into a file rebuilds it; for big archives that takes a while, so it is a separate, explicit choice.
        if (await Dialogs.ConfirmAsync(owner, "Write into the files?",
                $"The details were saved in the library only. Also write them into the {proposals.Count:N0} file(s) " +
                "themselves?\n\nEach file is rebuilt in the background and swapped in only after it verifies. Large " +
                "archives take a while. You can follow it on the Tasks page.", "Write into files", "Not now"))
            foreach (Proposal proposal in proposals) QueueFileWrite(proposal.Item);
    }

    /// <summary>The name to search the store for: the title without file-name noise, or nothing when there is none.</summary>
    private static string OnlineQueryName(LibraryItem item)
    {
        string name = Ps5MetadataSuggester.CleanName(item.Title);
        return name.Length > 0 ? name : Ps5MetadataSuggester.CleanName(Path.GetFileNameWithoutExtension(item.FileName));
    }

    /// <summary>
    /// Looks missing titles up in the PlayStation Store by name. Only titles that already have a Title ID are
    /// filled in, and only from a store entry with that same ID, because the same game has a different ID in each
    /// region. Each name is sent on request, one at a time.
    /// </summary>
    private async Task LookUpOnlineAsync()
    {
        Avalonia.Controls.Window? owner = Dialogs.OwnerOf(this);
        List<LibraryItem> missing = _items.Where(item => item.HasMissing).ToList();
        List<LibraryItem> candidates = missing.Where(item => item.TitleId.Length > 0).ToList();
        int noId = missing.Count(item => item.TitleId.Length == 0);
        if (candidates.Count == 0)
        {
            await Dialogs.ShowMessageAsync(owner, "Look up online",
                missing.Count == 0
                    ? "No title in the library is missing details."
                    : $"{missing.Count:N0} title(s) are missing details, but none can be looked up in bulk.\n\n" +
                      "A bulk lookup goes by Title ID, and none of them has one yet. Run Fix missing details first " +
                      "(it reads the ID from the file or folder name), or open a title, choose Edit details and " +
                      "search by name.");
            return;
        }
        if (!await Dialogs.ConfirmAsync(owner, "Look up online",
                $"Look up {candidates.Count:N0} title(s) by their Title ID?\n\n" +
                "The names and content IDs come from a public PS5 title list (a 2 MB file downloaded once from GitHub " +
                "and kept on this computer). Icons come from the PlayStation Store: only the game's name is sent, one " +
                "request about every second. Nothing is saved until you confirm the result." +
                (noId > 0 ? $"\n\n{noId:N0} other title(s) have no Title ID and are skipped." : string.Empty),
                "Look up", "Cancel"))
            return;

        var proposals = new List<Proposal>();
        var unmatched = new List<string>();
        int done = 0;
        try
        {
            foreach (LibraryItem item in candidates)
            {
                Status($"Looking up {item.TitleId}... {++done:N0} of {candidates.Count:N0}");
                IReadOnlyList<DbTitle> rows = await Ps5TitleDatabase.LookupAsync(item.TitleId,
                    _services.Store.AppDataDirectory);
                DbTitle? row = rows.FirstOrDefault();
                if (row is null)
                {
                    unmatched.Add(item.FileName);
                    continue;
                }
                var match = new StoreResult(row.Name, row.ContentId, row.TitleId, row.PublisherId, string.Empty,
                    string.Empty, string.Empty, 1.0);
                Proposal? proposal = await BuildOnlineProposalAsync(item, match);
                if (proposal is not null) proposals.Add(proposal);
            }
        }
        catch (OnlineLookupException ex)
        {
            Status(ex.Message);
            await Dialogs.ShowMessageAsync(owner, "Look up online", ex.Message);
            if (proposals.Count == 0) return;
        }

        if (proposals.Count == 0)
        {
            Status("Nothing new was found online.");
            await Dialogs.ShowMessageAsync(owner, "Look up online",
                "Nothing new was found." + (unmatched.Count > 0
                    ? $"\n\nNo store entry with the same Title ID was found for:\n{string.Join("\n", unmatched.Take(15))}" : string.Empty));
            return;
        }
        await ReviewSaveAndOfferWriteAsync(owner, "Look up online", proposals,
            $"{proposals.Count:N0} of {candidates.Count:N0} title(s) were found by Title ID." +
            (unmatched.Count > 0 ? $" {unmatched.Count:N0} had no entry with the same Title ID." : string.Empty));
    }

    /// <summary>Turns a confirmed store match into the fields that are still missing for the item.</summary>
    private static async Task<Proposal?> BuildOnlineProposalAsync(LibraryItem item, StoreResult match)
    {
        MetadataOverride? existing = MetadataOverrides.Get(item.Path);
        var suggestion = new MetadataSuggestion();
        // The file's real name beats a name guessed from the file name, but never replaces one the user typed.
        if (item.Game.LocalizedTitles.Count == 0 && string.IsNullOrWhiteSpace(existing?.Title))
        {
            suggestion.Title = match.Name;
            suggestion.Sources["Title"] = "public PS5 title list";
        }
        // A content ID the file really has, or one the user typed, stays. The offline guess (IV0000-...) does not.
        bool generated = existing?.ContentId.StartsWith("IV0000-", StringComparison.OrdinalIgnoreCase) == true;
        if (string.IsNullOrWhiteSpace(item.Game.ContentId) && (string.IsNullOrWhiteSpace(existing?.ContentId) || generated))
        {
            suggestion.ContentId = match.ContentId;
            suggestion.Sources["Content ID"] = $"public PS5 title list ({new DbTitle(match.TitleId, "", "", match.ContentId, match.ContentId.Length >= 2 ? match.ContentId[..2] : "", "").Region})";
        }

        byte[]? icon = null;
        if (existing is not { IconFile.Length: > 0 } && !await HasIconFileAsync(item.Game))
        {
            // The title list has no artwork: ask the store for it by the name found. A miss just means no icon.
            string iconUrl = match.IconUrl;
            if (iconUrl.Length == 0)
            {
                try
                {
                    iconUrl = (await Ps5StoreLookup.FindArtworkAsync(match.Name, match.TitleId))?.IconUrl ?? string.Empty;
                }
                catch (OnlineLookupException) { iconUrl = string.Empty; }
            }
            byte[]? downloaded = iconUrl.Length == 0 ? null : await Ps5StoreLookup.DownloadImageAsync(iconUrl);
            if (downloaded is not null)
            {
                icon = await IconTools.SquarePngAsync(Ps5ImageData.FromPng(downloaded));
                if (icon is not null) suggestion.IconSource = "the PlayStation Store";
            }
        }
        return suggestion.HasAnything || icon is not null ? new Proposal(item, suggestion, icon) : null;
    }

    private static Task<bool> HasIconFileAsync(Ps5GameInfo game) => Task.Run(() =>
    {
        try
        {
            using IReadOnlyGameFileSystem files = GameFileSystem.Open(game);
            return files.FileExists("sce_sys/icon0.png");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                       NotSupportedException or ArgumentException)
        {
            return false;
        }
    });

    private async void OnMoreLookUpOnline(object? sender, RoutedEventArgs e) => await LookUpOnlineAsync();

    /// <summary>Keeps only what is actually new for the item (the suggester already skips what the file has).</summary>
    private static void Trim(MetadataSuggestion suggestion, LibraryItem item)
    {
        MetadataOverride? existing = MetadataOverrides.Get(item.Path);
        // A value the user already entered by hand always wins.
        if (!string.IsNullOrWhiteSpace(existing?.Title)) { suggestion.Title = string.Empty; suggestion.Sources.Remove("Title"); }
        if (!string.IsNullOrWhiteSpace(existing?.TitleId)) { suggestion.TitleId = string.Empty; suggestion.Sources.Remove("Title ID"); }
        if (!string.IsNullOrWhiteSpace(existing?.ContentId)) { suggestion.ContentId = string.Empty; suggestion.Sources.Remove("Content ID"); }
        if (!string.IsNullOrWhiteSpace(existing?.Version)) { suggestion.Version = string.Empty; suggestion.Sources.Remove("Version"); }
        if (!string.IsNullOrWhiteSpace(existing?.Firmware)) { suggestion.Firmware = string.Empty; suggestion.Sources.Remove("Firmware"); }
        if (existing is { IconFile.Length: > 0 }) suggestion.Icon = null;
    }

    private static string Review(List<Proposal> proposals)
    {
        var text = new StringBuilder();
        foreach (Proposal proposal in proposals)
        {
            MetadataSuggestion s = proposal.Suggestion;
            text.AppendLine(proposal.Item.FileName);
            void Line(string label, string value)
            {
                if (value.Length == 0) return;
                text.AppendLine($"    {label,-12} {value}   ({s.Sources.GetValueOrDefault(label, "found")})");
            }
            Line("Title", s.Title);
            Line("Title ID", s.TitleId);
            Line("Content ID", s.ContentId);
            Line("Version", s.Version);
            Line("Firmware", s.Firmware);
            if (proposal.IconPng is not null)
                text.AppendLine($"    {"Icon",-12} " + (s.IconSource.Contains('.') ? $"made from sce_sys/{s.IconSource}" : $"from {s.IconSource}"));
            text.AppendLine();
        }
        return text.ToString();
    }

    private static void SaveProposal(Proposal proposal)
    {
        LibraryItem item = proposal.Item;
        MetadataSuggestion s = proposal.Suggestion;
        MetadataOverride edit = MetadataOverrides.Get(item.Path) ?? new MetadataOverride();
        if (s.Title.Length > 0) edit.Title = s.Title;
        if (s.TitleId.Length > 0) edit.TitleId = s.TitleId;
        if (s.ContentId.Length > 0) edit.ContentId = s.ContentId;
        if (s.Version.Length > 0) edit.Version = s.Version;
        if (s.Firmware.Length > 0) edit.Firmware = s.Firmware;
        if (proposal.IconPng is not null)
        {
            MetadataOverrides.DeleteIcon(edit.IconFile);
            edit.IconFile = MetadataOverrides.StoreIcon(item.Path, proposal.IconPng);
        }
        MetadataOverrides.Set(item.Path, edit);
        item.ApplyOverride(edit);
        item.ResetThumbnail();
        _ = item.Thumbnail;
    }

    /// <summary>Queues the item's saved details to be written into its file, replacing it in place.</summary>
    private void QueueFileWrite(LibraryItem item)
    {
        MetadataOverride? saved = MetadataOverrides.Get(item.Path);
        if (saved is null) return;
        var write = new MetadataEdit
        {
            Title = saved.Title, TitleId = saved.TitleId, ContentId = saved.ContentId,
            Version = saved.Version, Firmware = saved.Firmware,
            IconPath = MetadataOverrides.IconPath(saved) ?? string.Empty
        };
        if (write.IsEmpty) return;
        var spec = new JobSpec
        {
            Kind = JobSpec.EditDetails,
            Title = item.Title,
            Source = item.Path,
            FromPackage = item.Game.SourceKind == Ps5SourceKind.SonyPackage,
            Passcode = _services.Settings.DebugPasscode,
            Backend = _services.Settings.BuildBackend,
            Edit = write,
            Icon = write.IconPath.Length > 0 ? Convert.ToBase64String(File.ReadAllBytes(write.IconPath)) : null
        };
        string iconFile = saved.IconFile;
        QueuedPackageTask task = _services.EnqueueJob(spec, item.Title, "Edit details", item.Format, "In place");
        task.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (task.Status != PackageTaskStatus.Completed) return;
            // The file now holds these details, so the library-only copy is no longer needed.
            MetadataOverrides.Set(item.Path, new MetadataOverride());
            MetadataOverrides.DeleteIcon(iconFile);
            _ = RefreshAsync();
        });
    }

    private async void OnMoreFixMissing(object? sender, RoutedEventArgs e) => await FixMissingDetailsAsync();
}
