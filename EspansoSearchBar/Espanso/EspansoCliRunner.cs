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
    // IMPORTANT (verified against the espanso repository, scripts/resources/windows/):
    // the official Windows installer never ships an "espanso.exe" at all. It installs
    //   - espansod.exe   (the real binary; every CLI subcommand works on it directly)
    //   - espanso.cmd    (a one-line shim: @"%~dp0espansod.exe" %*)
    // so we must look for espansod.exe first and only fall back to espanso.exe for
    // portable/custom setups that may have renamed the binary.
    private const string DaemonExecutableName = "espansod.exe";
    private const string CliExecutableName = "espanso.exe";

    private static readonly object _resolveLock = new();
    private static string? _executableOverride;
    private static string? _cachedPath;

    /// <summary>
    /// Optional user-configured path (file or folder) from the extension settings. Setting a
    /// new value invalidates the cached resolution so the next call re-resolves.
    /// </summary>
    public static string? ExecutableOverride
    {
        get
        {
            lock (_resolveLock)
            {
                return _executableOverride;
            }
        }

        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            lock (_resolveLock)
            {
                if (!string.Equals(_executableOverride, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _executableOverride = normalized;
                    _cachedPath = null;
                }
            }
        }
    }

    /// <summary>
    /// Resolved full path to the espanso executable. Cached until the settings override changes.
    /// </summary>
    public static string ExecutablePath
    {
        get
        {
            lock (_resolveLock)
            {
                return _cachedPath ??= ResolveExecutablePath();
            }
        }
    }

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
    /// Finds the espanso executable. Order:
    ///   1. The user-configured override from the extension settings (file or folder).
    ///   2. Every directory on the process PATH *and* on the user PATH read straight from
    ///      the registry (HKCU\Environment). The registry read matters because espanso's
    ///      "env-path register" only writes HKCU\Environment\Path (espanso/src/path/win.rs),
    ///      and an MSIX-activated COM server like this extension does not inherit per-user
    ///      PATH changes made after logon - which is exactly why plain "espanso.exe" failed.
    ///   3. The default install folders of the official installer:
    ///      %LOCALAPPDATA%\Programs\Espanso (per-user) and %ProgramFiles%\Espanso (admin) -
    ///      Inno Setup "{autopf}\Espanso" resolves to one of these two.
    ///   4. Bare "espanso.exe" so Process.Start still tries OS PATH resolution as a last resort.
    /// In every directory we prefer espansod.exe (the real binary) over espanso.exe.
    /// </summary>
    private static string ResolveExecutablePath()
    {
        if (_executableOverride is { } configured)
        {
            if (File.Exists(configured))
            {
                return configured;
            }

            if (Directory.Exists(configured) && FindInDirectory(configured) is { } fromOverrideDir)
            {
                return fromOverrideDir;
            }
            // Fall through to auto-discovery if the configured path doesn't exist (better a
            // working fallback than a hard failure on a stale setting).
        }

        foreach (var directory in EnumerateCandidateDirectories())
        {
            if (FindInDirectory(directory) is { } found)
            {
                return found;
            }
        }

        return CliExecutableName;
    }

    private static string? FindInDirectory(string directory)
    {
        foreach (var name in new[] { DaemonExecutableName, CliExecutableName })
        {
            var candidate = Path.Combine(directory.Trim(), name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidateDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var processPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in processPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }
        }

        // User PATH from the registry - the authoritative location espanso writes to.
        var userPath = ReadUserPathFromRegistry();
        foreach (var directory in userPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }
        }

        // Official installer defaults (Inno Setup "{autopf}\Espanso").
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (localAppData.Length > 0)
        {
            yield return Path.Combine(localAppData, "Programs", "Espanso");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length > 0)
        {
            yield return Path.Combine(programFiles, "Espanso");
        }
    }

    private static string ReadUserPathFromRegistry()
    {
        try
        {
            using var environmentKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment");
            // GetValue expands REG_EXPAND_SZ entries (e.g. %LOCALAPPDATA%) by default.
            return environmentKey?.GetValue("Path") as string ?? string.Empty;
        }
        catch
        {
            // Registry access is best-effort; discovery continues with the other sources.
            return string.Empty;
        }
    }
}
