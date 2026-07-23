// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using EspansoSearchBar.Commands;
using EspansoSearchBar.Espanso;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace EspansoSearchBar.Pages;

/// <summary>
/// Main page of the extension: a searchable list of every espanso match. Espanso
/// "self management" moved out of this page - the service status/restart live on
/// <see cref="EspansoStatusPage"/> and the enable/disable toggle lives on the extension's
/// settings page (<see cref="SettingsManager"/>) - so the only non-match item here is
/// "Reload match list". The one exception is the disabled state: when espanso expansion is
/// (believed to be) off, the match list is replaced by a banner offering to re-enable it.
///
/// This is a <see cref="DynamicListPage"/> (not a plain ListPage) because filtering happens
/// as the user types, and reloading the (possibly large) match list on every keystroke would
/// be wasteful - EspansoClient caches the raw list for a few seconds and we filter it
/// in-memory here instead.
/// </summary>
internal sealed partial class EspansoSearchBarPage : DynamicListPage, IDisposable
{
    private readonly EspansoClient _client = new();
    private readonly SettingsManager _settingsManager;
    private readonly CancellationTokenSource _disposalCts = new();

    private IReadOnlyList<EspansoMatch> _lastLoadedMatches = [];
    private string? _lastLoadError;

    public EspansoSearchBarPage(SettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "Espanso Search Bar";
        Name = "Search";
        PlaceholderText = "Search espanso matches by trigger, replacement or label…";
        ShowDetails = false;

        // Kick off an initial background load so the list isn't empty the first time the
        // page is opened; RaiseItemsChanged() below refreshes the UI once it completes.
        _ = ReloadMatchesAsync(forceRefresh: false);
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged();

    public override IListItem[] GetItems()
    {
        var query = SearchText?.Trim() ?? string.Empty;

        var items = new List<IListItem>();

        if (!EspansoStateStore.AssumedEnabled)
        {
            // Requirement: if espanso is (believed to be) disabled, don't search matches at
            // all - offer only to re-enable it first. See EspansoStateStore for why this is a
            // best-effort assumption rather than a verified live status.
            items.Add(BuildDisabledBanner());
            items.Add(BuildEnableItem());
            items.Add(BuildToggleItem());
            return items.ToArray();
        }

        // Only show the reload item when the query is empty, so it doesn't crowd out match
        // results while searching for a trigger.
        if (query.Length == 0)
        {
            items.Add(BuildReloadItem());
        }

        items.AddRange(BuildMatchItems(query));

        if (_lastLoadError is not null)
        {
            items.Insert(0, new ListItem(new NoOpCommand())
            {
                Title = "Unable to load espanso matches",
                Subtitle = _lastLoadError,
                Icon = new IconInfo("\uE783"), // Error glyph.
            });
        }

        return items.ToArray();
    }

    private IListItem BuildDisabledBanner() => new ListItem(new NoOpCommand())
    {
        Title = "espanso expansion is disabled",
        Subtitle = "Matches are hidden while disabled. Enable espanso to search and trigger them again.",
        Icon = new IconInfo("\uE7BA"), // Warning glyph.
    };

    private IListItem BuildEnableItem() => new ListItem(BuildEnableCommand())
    {
        Title = "Enable espanso",
        Subtitle = "espanso cmd enable",
    };

    private IListItem BuildToggleItem() => new ListItem(BuildToggleCommand())
    {
        Title = "Toggle espanso",
        Subtitle = "espanso cmd toggle (use this if the extension's disabled/enabled guess is wrong)",
    };


    private IEnumerable<IListItem> BuildMatchItems(string query)
    {
        foreach (var match in _lastLoadedMatches)
        {
            if (query.Length > 0 && !MatchesQuery(match, query))
            {
                continue;
            }

            yield return new ListItem(new TriggerMatchCommand(match))
            {
                Title = match.PrimaryTrigger,
                Subtitle = match.Label is { Length: > 0 } label ? $"{label} — {match.DisplayReplacement}" : match.DisplayReplacement,
                Icon = new IconInfo("\uE97C"), // "TextField"-like glyph representing a snippet.
                MoreCommands =
                [
                    new CommandContextItem(new CopyReplacementCommand(match)),
                ],
            };
        }
    }

    private static bool MatchesQuery(EspansoMatch match, string query)
    {
        return match.Triggers.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))
            || match.Replace.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (match.Label?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private IListItem BuildReloadItem() => new ListItem(new RefreshMatchesCommand(this))
    {
        Title = "Reload match list",
        Subtitle = "Re-run 'espanso match list -j' (e.g. after editing your config)",
        Icon = new IconInfo("\uE72C"), // Sync glyph.
    };

    // The enable/toggle commands are only offered from the "espanso is disabled" banner view;
    // regular enable/disable management lives on the extension's settings page instead. Both
    // update the best-effort state assumption AND the persisted settings toggle so the two
    // surfaces stay consistent.
    private EspansoServiceCommand BuildEnableCommand() => new(
        "Enable espanso",
        "\uE73E", // Checkmark glyph.
        EspansoClient.EnableAsync,
        "espanso expansions enabled.",
        "Failed to enable espanso",
        onSuccess: () =>
        {
            EspansoStateStore.SetAssumedEnabled(true);
            _settingsManager.SyncEnabledState(true);
            RaiseItemsChanged();
        });

    private EspansoServiceCommand BuildToggleCommand() => new(
        "Toggle espanso",
        "\uE945", // Lightning/toggle glyph.
        EspansoClient.ToggleAsync,
        "espanso expansions toggled.",
        "Failed to toggle espanso",
        onSuccess: () =>
        {
            var newAssumption = !EspansoStateStore.AssumedEnabled;
            EspansoStateStore.SetAssumedEnabled(newAssumption);
            _settingsManager.SyncEnabledState(newAssumption);
            RaiseItemsChanged();
        });


    internal async Task ReloadMatchesAsync(bool forceRefresh)
    {
        try
        {
            _lastLoadedMatches = await _client.ListMatchesAsync(forceRefresh, _disposalCts.Token).ConfigureAwait(false);
            _lastLoadError = null;
        }
        catch (Exception ex)
        {
            _lastLoadError = ex.Message;
        }

        RaiseItemsChanged();
    }

    public void Dispose()
    {
        _disposalCts.Cancel();
        _disposalCts.Dispose();
    }
}
