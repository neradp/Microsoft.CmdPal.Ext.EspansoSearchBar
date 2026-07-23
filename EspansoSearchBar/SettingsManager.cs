// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using EspansoSearchBar.Espanso;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace EspansoSearchBar;

/// <summary>
/// Extension settings, surfaced by Command Palette on this extension's standard settings page
/// (the gear icon). Two settings are exposed:
///
///   - "Espanso executable path": optional override for where the espanso binary lives.
///     Normally auto-discovery in <see cref="EspansoCliRunner"/> finds it (registry user PATH
///     + default install folders), but portable installs in unusual locations need this.
///
///   - "Toggle automatic expansion": a stateless action disguised as a checkbox (the settings
///     form is a static Adaptive Card, so a checkbox is the only switch-like control
///     available). Checking it and saving runs "espanso cmd toggle" once and the checkbox
///     resets itself to unchecked; its value is never persisted as checked. It deliberately
///     does NOT try to display espanso's current on/off state: espanso offers no way to query
///     it ("espanso cmd enable|disable|toggle" are one-way events), so any stateful switch
///     would inevitably show stale/wrong values. The same Toggle (plus explicit Enable/
///     Disable) actions also live on <see cref="Pages.EspansoStatusPage"/>.
///
/// Settings persist to a JSON file under the packaged app's LocalState folder via the SDK's
/// JsonSettingsManager (Utilities.BaseSettingsPath handles the MSIX path redirection).
/// </summary>
internal sealed class SettingsManager : JsonSettingsManager
{
    private const string EspansoPathKey = "espansoPath";
    private const string ToggleExpansionKey = "toggleExpansion";

    private readonly TextSetting _espansoPath = new(
        EspansoPathKey,
        "Espanso executable path",
        "Optional. Full path to espansod.exe (or its folder). Leave empty to auto-detect from the user PATH and default install locations.",
        defaultValue: string.Empty)
    {
        Placeholder = "e.g. C:\\Users\\you\\espanso-portable\\espansod.exe",
    };

    private readonly ToggleSetting _toggleExpansion = new(
        ToggleExpansionKey,
        "Toggle automatic expansion",
        "Global espanso expansion switch ('espanso cmd toggle'). Saving with this checked applies it immediately and the checkbox resets itself; "
            + "espanso offers no way to query the real current state.",
        defaultValue: false);

    public string EspansoPath => _espansoPath.Value ?? string.Empty;

    public SettingsManager()
    {
        FilePath = SettingsJsonPath();

        Settings.Add(_espansoPath);
        Settings.Add(_toggleExpansion);

        LoadSettings();
        // A stale "true" from a previous crash mid-save must not re-toggle espanso at startup.
        _toggleExpansion.Value = false;
        ApplySideEffects();

        Settings.SettingsChanged += (_, _) => OnSettingsChanged();
    }

    private void OnSettingsChanged()
    {
        var toggleRequested = _toggleExpansion.Value;

        // Always reset the action checkbox before persisting, so "checked" is never stored
        // and the form comes back unchecked the next time it is opened.
        _toggleExpansion.Value = false;

        SaveSettings();
        ApplySideEffects();

        if (toggleRequested)
        {
            _ = RunToggleAsync();
        }
    }

    private static async Task RunToggleAsync()
    {
        try
        {
            var result = await EspansoClient.ToggleAsync().ConfigureAwait(false);
            if (!result.Succeeded)
            {
                ExtensionHost.LogMessage($"'espanso cmd toggle' from settings failed: {result.StandardError.Trim()}");
            }
        }
        catch (Exception ex)
        {
            ExtensionHost.LogMessage($"'espanso cmd toggle' from settings failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pushes setting values into the parts of the extension they control. Applied both at
    /// startup (so a previously saved override survives restarts) and on every save.
    /// </summary>
    private void ApplySideEffects()
    {
        EspansoCliRunner.ExecutableOverride = EspansoPath;
    }

    private static string SettingsJsonPath()
    {
        // Under MSIX this resolves to the package's redirected LocalState folder.
        var directory = Utilities.BaseSettingsPath("EspansoSearchBar");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "settings.json");
    }
}
