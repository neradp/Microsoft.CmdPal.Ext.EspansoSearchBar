// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;

namespace EspansoSearchBar;

/// <summary>
/// Root object exposed to Command Palette through the COM/WinRT boundary. The Guid below
/// must be identical to the "Id" used for the com:Class and CreateInstance ClassId entries
/// in Package.appxmanifest - Command Palette uses it to instantiate this class.
/// </summary>
[Guid("2B09F5F6-B403-4EF2-8420-928A6063E53F")]
public sealed partial class EspansoSearchBarExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _extensionDisposedEvent;

    private readonly EspansoSearchBarCommandsProvider _provider = new();

    public EspansoSearchBarExtension(ManualResetEvent extensionDisposedEvent)
    {
        _extensionDisposedEvent = extensionDisposedEvent;
    }

    /// <summary>
    /// Command Palette asks each extension for the providers it supports. We only implement
    /// the "Commands" provider type (top-level list page + fallback/invokable commands).
    /// </summary>
    public object? GetProvider(ProviderType providerType)
    {
        return providerType switch
        {
            ProviderType.Commands => _provider,
            _ => null,
        };
    }

    public void Dispose()
    {
        _provider.Dispose();
        _extensionDisposedEvent.Set();
    }
}
