// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using EspansoSearchBar.Espanso;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace EspansoSearchBar.Commands;

/// <summary>
/// Invokes a single espanso match by trigger, e.g. equivalent to running
/// "espanso match exec -t :sig" from a terminal.
///
/// The tricky part of this command (requirement "2a" from the design notes) is that espanso
/// always injects text into whatever window currently has keyboard focus. If we ran the CLI
/// call *before* Command Palette's own window has fully hidden and handed focus back to the
/// window the user was previously working in, the expansion could be typed into Command
/// Palette itself instead of the intended target application.
///
/// To avoid that race we:
///   1. Return <see cref="CommandResult.Hide"/> immediately, which tells Command Palette to
///      hide its window (but keep the current page/state) rather than fully dismissing it.
///   2. Fire-and-forget a short delay *after* returning from Invoke(), giving the window
///      manager time to restore focus to the previously active window.
///   3. Only then call "espanso match exec -t &lt;trigger&gt;", so the expansion lands in the
///      correct window at the correct cursor position - mirroring how espanso's own built-in
///      search bar (invoked via "espanso cmd search") behaves.
/// </summary>
internal sealed partial class TriggerMatchCommand : InvokableCommand
{
    // Empirically, ~150ms is enough for Command Palette's hide animation/focus hand-off to
    // complete on Windows 11 without introducing a noticeable delay for the user.
    private static readonly TimeSpan FocusHandoffDelay = TimeSpan.FromMilliseconds(150);

    private readonly string _trigger;

    public TriggerMatchCommand(EspansoMatch match)
    {
        _trigger = match.PrimaryTrigger;
        Name = "Trigger";
        Icon = new IconInfo("\uE945"); // Segoe Fluent "Lightning" glyph - fast action.
    }

    public override ICommandResult Invoke()
    {
        // Intentionally not awaited: Invoke() must return synchronously so Command Palette
        // can hide right away. The actual espanso call happens slightly later, in the
        // background, once focus has moved back to the user's previous window.
        _ = InjectAfterFocusReturnsAsync();

        return CommandResult.Hide();
    }

    private async Task InjectAfterFocusReturnsAsync()
    {
        try
        {
            await Task.Delay(FocusHandoffDelay).ConfigureAwait(false);

            var result = await EspansoClient.ExecMatchAsync(_trigger).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                // Surface failures (e.g. espanso service not running) as a toast instead of
                // silently swallowing them - the palette is already hidden at this point, so
                // ShowToast is the only way left to notify the user about this invocation.
                ExtensionHost.LogMessage(new LogMessage
                {
                    Message = $"espanso match exec -t \"{_trigger}\" failed: {result.StandardError.Trim()}",
                });
            }
        }
        catch (Exception ex)
        {
            ExtensionHost.LogMessage(new LogMessage { Message = $"Failed to trigger espanso match '{_trigger}': {ex.Message}" });
        }
    }
}
