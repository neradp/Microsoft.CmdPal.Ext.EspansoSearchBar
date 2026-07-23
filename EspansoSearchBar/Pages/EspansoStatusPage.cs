// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using EspansoSearchBar.Commands;
using EspansoSearchBar.Espanso;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace EspansoSearchBar.Pages;

/// <summary>
/// Small status page for the espanso background service. Shows whether the espanso daemon is
/// currently running (checked live via "espanso service status" every time the page is opened
/// or refreshed) plus a restart button.
///
/// Exit codes come straight from espanso's source (espanso/src/exit_code.rs +
/// cli/service/mod.rs): 0 = "espanso is running", 4 (SERVICE_NOT_RUNNING) = not running.
/// Note this is only about the *daemon process*; whether expansion is enabled or disabled is
/// a separate flag that espanso cannot report (see EspansoStateStore) and is controlled from
/// the extension settings toggle instead.
/// </summary>
internal sealed partial class EspansoStatusPage : ListPage
{
    private const int ServiceNotRunningExitCode = 4;

    private ServiceStatus _status = ServiceStatus.Unknown;
    private string? _statusError;

    private enum ServiceStatus
    {
        Unknown,
        Running,
        NotRunning,
        Error,
    }

    public EspansoStatusPage()
    {
        Icon = new IconInfo("\uE9D9"); // Diagnostic glyph.
        Title = "Espanso Status";
        Name = "Open";

        _ = RefreshStatusAsync();
    }

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>
        {
            BuildStatusItem(),
            new ListItem(new EspansoServiceCommand(
                "Restart espanso",
                "\uE777", // Refresh glyph.
                EspansoClient.RestartAsync,
                "espanso restarted successfully.",
                "Failed to restart espanso",
                onSuccess: () => _ = RefreshStatusAsync()))
            {
                Subtitle = "espanso service restart",
            },
            new ListItem(new AnonymousCommand(() => _ = RefreshStatusAsync())
            {
                Name = "Refresh status",
                Icon = new IconInfo("\uE72C"), // Sync glyph.
                Result = CommandResult.KeepOpen(),
            })
            {
                Title = "Refresh status",
                Subtitle = "Re-run 'espanso service status'",
            },
        };

        return items.ToArray();
    }

    private ListItem BuildStatusItem()
    {
        var (title, subtitle, glyph) = _status switch
        {
            ServiceStatus.Running => ("espanso service is running", "'espanso service status' reported a running daemon (exit code 0)", "\uEC61"), // StatusCircleCheckmark.
            ServiceStatus.NotRunning => ("espanso service is NOT running", "Use 'Restart espanso' below to start it (exit code 4, SERVICE_NOT_RUNNING)", "\uEB90"), // StatusErrorFull.
            ServiceStatus.Error => ("Unable to determine espanso status", _statusError ?? "Unknown error", "\uE7BA"), // Warning.
            _ => ("Checking espanso status…", "Running 'espanso service status'", "\uE9D9"), // Diagnostic.
        };

        return new ListItem(new NoOpCommand())
        {
            Title = title,
            Subtitle = subtitle,
            Icon = new IconInfo(glyph),
        };
    }

    private async Task RefreshStatusAsync()
    {
        _status = ServiceStatus.Unknown;
        _statusError = null;
        RaiseItemsChanged();

        try
        {
            var result = await EspansoClient.GetStatusAsync().ConfigureAwait(false);
            _status = result.ExitCode switch
            {
                0 => ServiceStatus.Running,
                ServiceNotRunningExitCode => ServiceStatus.NotRunning,
                _ => ServiceStatus.Error,
            };

            if (_status == ServiceStatus.Error)
            {
                _statusError = $"'espanso service status' exited with code {result.ExitCode}: {result.StandardError.Trim()}";
            }
        }
        catch (Exception ex)
        {
            _status = ServiceStatus.Error;
            _statusError = ex.Message;
        }

        RaiseItemsChanged();
    }
}
