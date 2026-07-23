// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;
using System;
using System.Threading;

namespace EspansoSearchBar;

/// <summary>
/// Process entry point. Command Palette never launches this executable directly for the
/// user - instead, Windows activates it as an out-of-process COM server (declared in
/// Package.appxmanifest) whenever Command Palette needs our extension. See:
/// https://learn.microsoft.com/windows/powertoys/command-palette/extensibility-overview
/// </summary>
public class Program
{
    [MTAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            ComServer server = new();

            ManualResetEvent extensionDisposedEvent = new(false);

            // A single instance of the extension is created and reused for every activation
            // request coming from Command Palette, so all pages/commands share the same
            // EspansoClient (and therefore the same cached match list / cli state).
            EspansoSearchBarExtension extensionInstance = new(extensionDisposedEvent);
            server.RegisterClass<EspansoSearchBarExtension, IExtension>(() => extensionInstance);
            server.Start();

            // Block the process until the host disposes our extension object (e.g. Command
            // Palette is closing, or the user ran "Reload Command Palette extensions").
            extensionDisposedEvent.WaitOne();
            server.Stop();
            server.UnsafeDispose();
        }
        else
        {
            Console.WriteLine("EspansoSearchBar is a Command Palette extension and is not meant to be run directly.");
        }
    }
}
