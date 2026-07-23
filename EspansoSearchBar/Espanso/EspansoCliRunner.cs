// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Text;

namespace EspansoSearchBar.Espanso;

/// <summary>
/// Result of running an espanso CLI invocation.
/// </summary>
/// <param name="ExitCode">Process exit code (0 on success, per espanso's exit_code module).</param>
/// <param name="StandardOutput">Captured stdout.</param>
/// <param name="StandardError">Captured stderr.</param>
public readonly record struct EspansoCliResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Thin wrapper around the "espanso" executable. Every call to espanso is a short-lived,
/// hidden (no console window), redirected-output child process - this class owns all the
/// <see cref="Process"/> plumbing so the rest of the extension only ever talks in terms of
/// espanso commands and results.
///
/// Reference: https://espanso.org/docs/cli/ (espanso match list -j / espanso match exec -t)
/// and the upstream CLI source under espanso/espanso/src/cli, which we used to confirm the
/// exact subcommands and JSON schema this extension relies on.
/// </summary>
public sealed class EspansoCliRunner
{
    private const string ExecutableName = "espanso.exe";

    /// <summary>
    /// Resolved full path to espanso.exe, or just "espanso.exe" if we rely on PATH resolution.
    /// Cached lazily since it never changes for the process lifetime.
    /// </summary>
    private static readonly Lazy<string> _resolvedExecutable = new(ResolveExecutablePath);

    public static string ExecutablePath => _resolvedExecutable.Value;

    /// <summary>
    /// Runs "espanso &lt;arguments&gt;" and captures its output. Never throws for a non-zero
    /// exit code - callers should inspect <see cref="EspansoCliResult.Succeeded"/> - but does
    /// throw if the executable itself cannot be found/started, which the caller should catch
    /// and surface as a friendly error (e.g. "espanso is not installed or not in PATH").
    /// </summary>
    public static async Task<EspansoCliResult> RunAsync(IEnumerable<string> arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var effectiveTimeout = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, effectiveTimeout.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // We hit our own timeout, not the caller's cancellation - kill the runaway process.
            TryKill(process);
            throw new TimeoutException($"espanso {string.Join(' ', arguments)} did not complete in time.");
        }

        return new EspansoCliResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort only - the process may have exited between the check and the kill.
        }
    }

    /// <summary>
    /// Looks for espanso.exe first on PATH, then in the well-known per-user install location
    /// used by the official espanso Windows installer. Falls back to the bare executable name
    /// so that Process.Start still attempts PATH resolution as a last resort.
    /// </summary>
    private static string ResolveExecutablePath()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), ExecutableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Default install path for the espanso Windows installer (per-user, no admin required).
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultInstallPath = Path.Combine(localAppData, "Programs", "Espanso", ExecutableName);
        if (File.Exists(defaultInstallPath))
        {
            return defaultInstallPath;
        }

        return ExecutableName;
    }
}
