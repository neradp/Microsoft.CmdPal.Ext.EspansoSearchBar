// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using EspansoSearchBar.Commands;
using EspansoSearchBar.Espanso;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace EspansoSearchBar.Pages;

/// <summary>
/// Main page of the extension: a searchable list of every espanso match. Espanso
/// "self management" lives on <see cref="EspansoStatusPage"/> (service status/restart +
/// enable/disable/toggle of automatic expansion), so the only non-match item here is
/// "Reload match list".
///
/// Note that matches are listed and triggerable even while espanso's automatic expansion is
/// disabled ("espanso cmd disable"): verified in espanso's source, the runtime toggle lives
/// in DisableMiddleware (espanso-engine/src/process/middleware/disable.rs), which only blocks
/// *keyboard* events while disabled - a "match exec" IPC request still expands normally.
/// Manually triggering matches with automatic expansion switched off is a perfectly valid
/// workflow.
///
/// This is a <see cref="DynamicListPage"/> (not a plain ListPage) because filtering happens
/// as the user types, and reloading the (possibly large) match list on every keystroke would
/// be wasteful - EspansoClient caches the raw list for a few seconds and we filter it
/// in-memory here instead.
/// </summary>
internal sealed partial class EspansoSearchBarPage : DynamicListPage, IDisposable
{
    private readonly EspansoClient _client = new();
    private readonly CancellationTokenSource _disposalCts = new();

    private IReadOnlyList<EspansoMatch> _lastLoadedMatches = [];
    private string? _lastLoadError;

    public EspansoSearchBarPage()
    {
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

    private IEnumerable<IListItem> BuildMatchItems(string query)
    {
        foreach (var match in _lastLoadedMatches)
        {
            if (query.Length > 0 && !MatchesQuery(match, query))
            {
                continue;
            }

            // Mirror espanso's own search bar layout: the content (label if present,
            // otherwise the replacement preview) is the primary text, and the trigger is
            // shown as a tag on the right. Multi-line/long replacements are flattened and
            // truncated by DisplayReplacement.
            var hasLabel = match.Label is { Length: > 0 };
            yield return new ListItem(new TriggerMatchCommand(match))
            {
                Title = hasLabel ? match.Label! : match.DisplayReplacement,
                Subtitle = hasLabel ? match.DisplayReplacement : string.Empty,
                Icon = new IconInfo("\uE97C"), // "TextField"-like glyph representing a snippet.
                Tags = match.Triggers.Where(t => t.Length > 0).Select(ITag (t) => new Tag(t)).ToArray(),
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
