// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using EspansoSearchBar.Pages;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace EspansoSearchBar;

/// <summary>
/// Top-level entry point Command Palette shows for this extension. Exposes a single command
/// ("Espanso Search Bar") that opens <see cref="EspansoSearchBarPage"/>.
/// </summary>
public partial class EspansoSearchBarCommandsProvider : CommandProvider
{
    private readonly EspansoSearchBarPage _page = new();
    private readonly ICommandItem[] _commands;

    public EspansoSearchBarCommandsProvider()
    {
        DisplayName = "Espanso Search Bar";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        _commands =
        [
            new CommandItem(_page)
            {
                Title = DisplayName,
                Subtitle = "Search and trigger your espanso matches",
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
