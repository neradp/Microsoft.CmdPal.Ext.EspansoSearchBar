// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

namespace EspansoSearchBar.Espanso;

/// <summary>
/// Best-effort, in-memory tracking of whether espanso's global expansion toggle is currently
/// enabled.
///
/// IMPORTANT LIMITATION (verified against espanso's source, not assumed): espanso does not
/// expose any public CLI/IPC query to read the *current* enabled/disabled state.
///   - "espanso cmd enable|disable|toggle" (espanso/src/cli/cmd.rs) are fire-and-forget IPC
///     events with no reply.
///   - "espanso service status" (espanso/src/cli/service/mod.rs) only reports whether the
///     background process is running, not whether expansion is enabled.
///   - "espanso match exec -t &lt;trigger&gt;" itself gives no feedback either way: internally
///     it is converted into the very same "MatchesDetected" event a normal keystroke trigger
///     would produce (espanso-engine/src/process/middleware/match_exec.rs), which is then
///     silently turned into a NOOP by "SuppressMiddleware" whenever expansion is disabled
///     (espanso-engine/src/process/middleware/suppress.rs) - and the CLI call still reports
///     success (exit code 0) either way, because it sends the request asynchronously
///     (espanso/src/cli/match_cli/exec.rs uses IPCClient::send_async).
///
/// So this class can only track the state *we* last set through this extension's own Enable/
/// Disable/Toggle commands. If the user disables espanso some other way (tray icon, the
/// default Alt+Shift+X global hotkey, another tool), our assumption goes stale until the user
/// interacts with one of our own toggle commands again. This is a deliberate, documented
/// trade-off given espanso's current CLI surface - not a bug.
/// </summary>
public static class EspansoStateStore
{
    // Optimistic default: most users have expansion enabled most of the time.
    private static volatile bool _assumedEnabled = true;

    public static bool AssumedEnabled => _assumedEnabled;

    public static void SetAssumedEnabled(bool enabled) => _assumedEnabled = enabled;
}
