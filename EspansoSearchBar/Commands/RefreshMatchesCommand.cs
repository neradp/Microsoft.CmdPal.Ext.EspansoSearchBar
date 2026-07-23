// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using EspansoSearchBar.Pages;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace EspansoSearchBar.Commands;

/// <summary>
/// Forces a fresh "espanso match list -j" call, bypassing <see cref="EspansoSearchBar.Espanso.EspansoClient"/>'s
/// short-lived cache. Useful right after editing a match YAML file, since espanso itself
/// hot-reloads its config but this extension would otherwise keep serving the cached list for
/// a few seconds.
/// </summary>
internal sealed partial class RefreshMatchesCommand(EspansoSearchBarPage page) : InvokableCommand
{
    public override ICommandResult Invoke()
    {
        _ = page.ReloadMatchesAsync(forceRefresh: true);
        return CommandResult.KeepOpen();
    }
}
