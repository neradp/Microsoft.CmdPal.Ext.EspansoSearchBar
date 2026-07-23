// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using System.Text.Json;

namespace EspansoSearchBar.Espanso;

/// <summary>
/// High level espanso operations used by this extension. All CLI subcommands used here were
/// verified against the upstream espanso source (github.com/espanso/espanso):
///   - "espanso match list -j"      -> espanso/src/cli/match_cli/list.rs
///   - "espanso match exec -t X"    -> espanso/src/cli/match_cli/exec.rs
///   - "espanso service status"     -> espanso/src/cli/service/mod.rs
///   - "espanso service restart"    -> espanso/src/cli/service/mod.rs
///   - "espanso cmd enable|disable|toggle" -> espanso/src/cli/cmd.rs
/// See also the official CLI docs: https://espanso.org/docs/cli/
/// </summary>
public sealed class EspansoClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Short in-memory cache so re-opening the palette feels instant; refreshed on demand.</summary>
    private List<EspansoMatch>? _cachedMatches;
    private DateTimeOffset _cachedAt;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Returns the list of configured matches, using a short-lived cache to keep the list
    /// page snappy while typing. Pass <paramref name="forceRefresh"/> to bypass the cache
    /// (e.g. after the user edits their espanso config and asks to reload).
    /// </summary>
    public async Task<IReadOnlyList<EspansoMatch>> ListMatchesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cachedMatches is not null && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
        {
            return _cachedMatches;
        }

        var result = await EspansoCliRunner.RunAsync(["match", "list", "-j"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new EspansoCliException($"'espanso match list -j' failed: {result.StandardError.Trim()}");
        }

        var matches = JsonSerializer.Deserialize<List<EspansoMatch>>(result.StandardOutput, JsonOptions) ?? [];

        // Matches with no textual trigger (regex-only causes) are reported by espanso as the
        // literal trigger "(none)" - they cannot be launched via "match exec -t", so we hide them.
        matches.RemoveAll(m => m.Triggers.Count == 0 || (m.Triggers.Count == 1 && m.Triggers[0] == "(none)"));

        _cachedMatches = matches;
        _cachedAt = DateTimeOffset.UtcNow;
        return matches;
    }

    /// <summary>
    /// Requests that espanso's worker process expand (type out) the given match trigger at
    /// the current cursor position, exactly as "espanso match exec -t &lt;trigger&gt;" does
    /// on the command line. Requires the espanso background service to be running.
    /// </summary>
    public static async Task<EspansoCliResult> ExecMatchAsync(string trigger, CancellationToken cancellationToken = default)
    {
        return await EspansoCliRunner.RunAsync(["match", "exec", "-t", trigger], cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static Task<EspansoCliResult> GetStatusAsync(CancellationToken cancellationToken = default) =>
        EspansoCliRunner.RunAsync(["service", "status"], cancellationToken: cancellationToken);

    public static Task<EspansoCliResult> RestartAsync(CancellationToken cancellationToken = default) =>
        EspansoCliRunner.RunAsync(["service", "restart"], timeout: TimeSpan.FromSeconds(15), cancellationToken: cancellationToken);

    public static Task<EspansoCliResult> StartAsync(CancellationToken cancellationToken = default) =>
        EspansoCliRunner.RunAsync(["service", "start"], timeout: TimeSpan.FromSeconds(15), cancellationToken: cancellationToken);

    public static Task<EspansoCliResult> StopAsync(CancellationToken cancellationToken = default) =>
        EspansoCliRunner.RunAsync(["service", "stop"], cancellationToken: cancellationToken);

    public static Task<EspansoCliResult> EnableAsync(CancellationToken cancellationToken = default) =>
        EspansoCliRunner.RunAsync(["cmd", "enable"], cancellationToken: cancellationToken);

    public static Task<EspansoCliResult> DisableAsync(CancellationToken cancellationToken = default) =>
        EspansoCliRunner.RunAsync(["cmd", "disable"], cancellationToken: cancellationToken);

    public static Task<EspansoCliResult> ToggleAsync(CancellationToken cancellationToken = default) =>
        EspansoCliRunner.RunAsync(["cmd", "toggle"], cancellationToken: cancellationToken);
}

/// <summary>Thrown when an espanso CLI invocation fails or returns unparsable output.</summary>
public sealed class EspansoCliException(string message) : Exception(message);
