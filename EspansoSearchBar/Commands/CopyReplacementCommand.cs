// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using EspansoSearchBar.Espanso;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace EspansoSearchBar.Commands;

/// <summary>
/// Secondary ("more commands") action on a match list item: copies the expanded replacement
/// text to the clipboard without invoking espanso at all. Useful when the target window isn't
/// focused yet, or when the user just wants the text to paste manually.
/// </summary>
internal sealed partial class CopyReplacementCommand : InvokableCommand
{
    private readonly string _replacement;

    public CopyReplacementCommand(EspansoMatch match)
    {
        _replacement = match.Replace;
        Name = "Copy replacement";
        Icon = new IconInfo("\uE8C8"); // Copy glyph.
    }

    public override ICommandResult Invoke()
    {
        // ClipboardHelper is the SDK's own Win32-based clipboard wrapper; unlike the WinRT
        // Clipboard API it doesn't require a UI thread/CoreWindow, which an out-of-process
        // extension like this one doesn't have.
        ClipboardHelper.SetText(_replacement);

        return CommandResult.ShowToast("Replacement copied to clipboard.");
    }
}
