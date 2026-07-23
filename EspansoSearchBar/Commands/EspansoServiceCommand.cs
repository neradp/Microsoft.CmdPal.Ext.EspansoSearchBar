// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using EspansoSearchBar.Espanso;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace EspansoSearchBar.Commands;

/// <summary>
/// Delegate used by <see cref="EspansoServiceCommand"/> to run one specific espanso CLI
/// operation (e.g. "espanso service restart" or "espanso cmd toggle").
/// </summary>
internal delegate Task<EspansoCliResult> EspansoOperation(CancellationToken cancellationToken);

/// <summary>
/// Generic command for the small set of espanso "self management" CLI subcommands that don't
/// need a match/trigger as input: service status/start/stop/restart and cmd enable/disable/
/// toggle (https://espanso.org/docs/cli/). Unlike <see cref="TriggerMatchCommand"/>, these are
/// administrative actions, so we keep Command Palette open (<see cref="CommandResult.KeepOpen"/>)
/// and report the outcome via a toast instead of hiding the window.
/// </summary>
internal sealed partial class EspansoServiceCommand : InvokableCommand
{
    private readonly EspansoOperation _operation;
    private readonly string _successMessage;
    private readonly string _failurePrefix;
    private readonly Action? _onSuccess;

    /// <param name="onSuccess">
    /// Optional callback invoked after a successful CLI call, used by the page to update
    /// <see cref="EspansoStateStore"/>'s best-effort enabled/disabled assumption and refresh
    /// the list. Not invoked on failure, since we can't tell whether the CLI call actually
    /// changed anything (see EspansoStateStore's remarks on espanso's fire-and-forget IPC).
    /// </param>
    public EspansoServiceCommand(string name, string glyph, EspansoOperation operation, string successMessage, string failurePrefix, Action? onSuccess = null)
    {
        Name = name;
        Icon = new IconInfo(glyph);
        _operation = operation;
        _successMessage = successMessage;
        _failurePrefix = failurePrefix;
        _onSuccess = onSuccess;
    }

    public override ICommandResult Invoke()
    {
        _ = RunAsync();
        return CommandResult.KeepOpen();
    }

    private async Task RunAsync()
    {
        try
        {
            var result = await _operation(CancellationToken.None).ConfigureAwait(false);
            var message = result.Succeeded
                ? _successMessage
                : $"{_failurePrefix}: {result.StandardError.Trim()}";

            var status = new StatusMessage
            {
                Message = message,
                State = result.Succeeded ? MessageState.Success : MessageState.Error,
            };

            // ExtensionHost.ShowStatus only takes the message itself - there is no built-in
            // auto-dismiss timeout, so we show it, wait a bit, then hide it ourselves.
            ExtensionHost.ShowStatus(status);
            _ = AutoHideAsync(status);

            if (result.Succeeded)
            {
                _onSuccess?.Invoke();
            }
        }
        catch (Exception ex)
        {
            var status = new StatusMessage { Message = $"{_failurePrefix}: {ex.Message}", State = MessageState.Error };
            ExtensionHost.ShowStatus(status);
            _ = AutoHideAsync(status);
        }
    }

    private static async Task AutoHideAsync(StatusMessage status)
    {
        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        ExtensionHost.HideStatus(status);
    }
}
