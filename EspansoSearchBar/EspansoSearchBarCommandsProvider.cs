// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using EspansoSearchBar.Pages;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace EspansoSearchBar;

/// <summary>
/// Top-level entry point Command Palette shows for this extension. Exposes two commands:
/// "Espanso Search Bar" (<see cref="EspansoSearchBarPage"/>, the searchable match list) and
/// "Espanso Status" (<see cref="EspansoStatusPage"/>, live service status + restart), plus a
/// standard settings page (<see cref="SettingsManager"/>) with the enable/disable toggle and
/// the executable path override.
/// </summary>
public partial class EspansoSearchBarCommandsProvider : CommandProvider
{
    private readonly SettingsManager _settingsManager = new();
    private readonly EspansoSearchBarPage _page;
    private readonly EspansoStatusPage _statusPage = new();
    private readonly ICommandItem[] _commands;

    public EspansoSearchBarCommandsProvider()
    {
        DisplayName = "Espanso Search Bar";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Settings = _settingsManager.Settings;

        _page = new EspansoSearchBarPage(_settingsManager);

        _commands =
        [
            new CommandItem(_page)
            {
                Title = DisplayName,
                Subtitle = "Search and trigger your espanso matches",
            },
            new CommandItem(_statusPage)
            {
                Title = "Espanso Status",
                Subtitle = "Check whether the espanso service is running, restart it if needed",
            },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override void Dispose()
    {
        GC.SuppressFinalize(this);
        base.Dispose();
        _page.Dispose();
    }
}
