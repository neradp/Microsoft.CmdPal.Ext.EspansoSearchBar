// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using EspansoSearchBar.Espanso;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace EspansoSearchBar;

/// <summary>
/// Extension settings, surfaced by Command Palette on this extension's standard settings page
/// (the gear icon). Two settings are exposed:
///
///   - "Espanso enabled": a toggle bound to espanso's global expansion switch. Saving the
///     settings form with a changed value runs "espanso cmd enable" / "espanso cmd disable"
///     and updates <see cref="EspansoStateStore"/>'s best-effort assumption. Note that the
///     settings form is a static Adaptive Card, so the toggle reflects the state at the time
///     the page was opened (there is no live status inside the settings UI).
///
///   - "Espanso executable path": optional override for where the espanso binary lives.
///     Normally auto-discovery in <see cref="EspansoCliRunner"/> finds it (registry user PATH
///     + default install folders), but portable installs in unusual locations need this.
///
/// Settings persist to a JSON file under the packaged app's LocalState folder via the SDK's
/// JsonSettingsManager (Utilities.BaseSettingsPath handles the MSIX path redirection).
/// </summary>
internal sealed class SettingsManager : JsonSettingsManager
{
    private const string EspansoEnabledKey = "espansoEnabled";
    private const string EspansoPathKey = "espansoPath";

    private readonly ToggleSetting _espansoEnabled = new(
        EspansoEnabledKey,
        "Espanso enabled",
        "Global espanso expansion switch ('espanso cmd enable/disable'). Saving a change applies it immediately. "
            + "This reflects the last state set through this extension; espanso offers no way to query the real current state.",
        defaultValue: true);

    private readonly TextSetting _espansoPath = new(
        EspansoPathKey,
        "Espanso executable path",
        "Optional. Full path to espansod.exe (or its folder). Leave empty to auto-detect from the user PATH and default install locations.",
        defaultValue: string.Empty)
    {
        Placeholder = "e.g. C:\\Users\\you\\espanso-portable\\espansod.exe",
    };

    public bool EspansoEnabled => _espansoEnabled.Value;

    public string EspansoPath => _espansoPath.Value ?? string.Empty;

    public SettingsManager()
    {
        FilePath = SettingsJsonPath();

        Settings.Add(_espansoEnabled);
        Settings.Add(_espansoPath);

        LoadSettings();
        ApplySideEffects(applyEnabledState: false);

        Settings.SettingsChanged += (_, _) => OnSettingsChanged();
    }

    private void OnSettingsChanged()
    {
        SaveSettings();
        ApplySideEffects(applyEnabledState: true);
    }

    /// <summary>
    /// Pushes setting values into the parts of the extension they control. The executable
    /// path is always applied (including at startup, so a previously saved override survives
    /// restarts); the enabled state is only applied on explicit user changes, because at
    /// startup we must not silently flip espanso to whatever the stored value happens to be.
    /// </summary>
    private void ApplySideEffects(bool applyEnabledState)
    {
        EspansoCliRunner.ExecutableOverride = EspansoPath;

        if (!applyEnabledState)
        {
            // Startup: adopt the persisted toggle as our best-effort assumption without
            // invoking the CLI (we must not silently flip espanso's real state on load).
            EspansoStateStore.SetAssumedEnabled(EspansoEnabled);
            return;
        }

        var enable = EspansoEnabled;
        if (enable == EspansoStateStore.AssumedEnabled)
        {
            return; // No change - don't spam espanso with redundant IPC events.
        }

        _ = ApplyEnabledStateAsync(enable);
    }

    private static async Task ApplyEnabledStateAsync(bool enable)
    {
        try
        {
            var result = enable
                ? await EspansoClient.EnableAsync().ConfigureAwait(false)
                : await EspansoClient.DisableAsync().ConfigureAwait(false);

            if (result.Succeeded)
            {
                EspansoStateStore.SetAssumedEnabled(enable);
            }
            else
            {
                ExtensionHost.LogMessage($"Applying espanso enabled={enable} from settings failed: {result.StandardError.Trim()}");
            }
        }
        catch (Exception ex)
        {
            ExtensionHost.LogMessage($"Applying espanso enabled={enable} from settings failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps the settings toggle in sync when the enabled state changes from elsewhere in the
    /// extension (status page buttons, the "Enable espanso" item on the disabled banner).
    /// Persists without re-triggering the enable/disable CLI side effect.
    /// </summary>
    public void SyncEnabledState(bool enabled)
    {
        if (_espansoEnabled.Value == enabled)
        {
            return;
        }

        _espansoEnabled.Value = enabled;
        SaveSettings();
    }

    private static string SettingsJsonPath()
    {
        // Under MSIX this resolves to the package's redirected LocalState folder.
        var directory = Utilities.BaseSettingsPath("EspansoSearchBar");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "settings.json");
    }
}
