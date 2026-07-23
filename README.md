# Espanso Search Bar — a PowerToys Command Palette extension

This project replaces espanso's built-in search bar (`espanso cmd search`) with a
[PowerToys Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview)
extension, so that browsing, filtering and triggering
[espanso](https://espanso.org) text-expansion matches happens inside the same launcher
Windows power users already use for everything else.

## 1. Requirements (from `AGENTS.md`)

1. List espanso matches, and some commands for espanso itself.
2. Let the user execute ("trigger") the selected match.
   - a. Hide the Command Palette window first, so focus returns to whatever window/cursor
     the user was working in — espanso injects text into the currently focused control.
   - b. Trigger the match from the command line (`espanso match exec -t <trigger>`), i.e.
     don't reimplement espanso's expansion logic — just drive the real espanso CLI/IPC.

## 2. How it works (architecture)

```
┌─────────────────────────┐   COM/WinRT (out-of-process)   ┌───────────────────────────┐
│   PowerToys Command      │ ───────────────────────────── │  EspansoSearchBar.exe      │
│   Palette (host process) │ ◄───────────────────────────  │  (this extension)          │
└─────────────────────────┘                                 └─────────────┬─────────────┘
                                                                            │ Process.Start
                                                                            ▼
                                                              ┌───────────────────────────┐
                                                              │  espanso.exe (CLI)         │
                                                              │  - match list -j           │
                                                              │  - match exec -t <trig>    │
                                                              │  - service status/restart  │
                                                              │  - cmd enable/disable/toggle│
                                                              └─────────────┬─────────────┘
                                                                            │ IPC (named pipe)
                                                                            ▼
                                                              ┌───────────────────────────┐
                                                              │  espanso worker process    │
                                                              │  (the actual background    │
                                                              │   service that types text) │
                                                              └───────────────────────────┘
```

Command Palette extensions are standalone, isolated .NET processes activated by Windows as an
out-of-process COM server — never a DLL loaded in-proc into Command Palette. This is described
in the official docs:
https://learn.microsoft.com/windows/powertoys/command-palette/extensibility-overview

This extension never talks to espanso's IPC socket directly. Instead it always shells out to
the real `espanso` executable, exactly as requested in `AGENTS.md` ("triger the match from
command line"). This keeps the extension small, and guarantees behavior stays in sync with
whatever espanso version the user has installed, since we're using its public, documented CLI:
https://espanso.org/docs/cli/

## 3. Project layout

```
EspansoSearchBar.sln
EspansoSearchBar/
├── Program.cs                          Process entry point / COM server bootstrap
├── EspansoSearchBarExtension.cs        IExtension implementation (COM root object)
├── EspansoSearchBarCommandsProvider.cs Top-level command ("Espanso Search Bar")
├── Package.appxmanifest                MSIX manifest: COM registration + CmdPalProvider
├── app.manifest                        Win32 app manifest (DPI awareness, OS compat)
├── Espanso/
│   ├── EspansoMatch.cs                 JSON model for "espanso match list -j" entries
│   ├── EspansoCliRunner.cs             Low-level hidden-process runner for espanso.exe
│   └── EspansoClient.cs                High-level espanso operations used by the UI
├── Pages/
│   └── EspansoSearchBarPage.cs         DynamicListPage: searchable match list + actions
└── Commands/
    ├── TriggerMatchCommand.cs          Hides palette, then runs "match exec -t <trigger>"
    ├── CopyReplacementCommand.cs       Alternate action: copy replacement to clipboard
    ├── EspansoServiceCommand.cs        Generic wrapper for service/cmd CLI subcommands
    └── RefreshMatchesCommand.cs        Forces a fresh "match list -j" (bypasses cache)
```

This mirrors the exact structure Command Palette's own "Create a new extension" wizard
generates (verified against the real generator template in the PowerToys repository:
`src/modules/cmdpal/ExtensionTemplate/TemplateCmdPalExtension`), plus an `Espanso/` folder for
the CLI integration and a `Commands/` folder for the extra actions this extension adds.

## 4. Espanso CLI surface used (verified against espanso's own source)

All subcommands below were cross-checked against the upstream Rust source at
https://github.com/espanso/espanso (not just blog posts), to make sure the arguments and JSON
shape used in `Espanso/EspansoClient.cs` are accurate:

| Command | Verified in | Purpose |
|---|---|---|
| `espanso match list -j` | `espanso/src/cli/match_cli/list.rs` | Lists all configured matches as JSON: `[{ "triggers": [...], "replace": "...", "label": "..." }]` |
| `espanso match exec -t <trigger>` | `espanso/src/cli/match_cli/exec.rs` | Asks the running espanso *worker* process to expand a match by trigger at the current cursor position. Fails if the worker isn't running. |
| `espanso service status` | `espanso/src/cli/service/mod.rs` | Reports whether the background service is running. |
| `espanso service restart` / `start` / `stop` | `espanso/src/cli/service/mod.rs` | Manage the background service. |
| `espanso cmd enable` / `disable` / `toggle` | `espanso/src/cli/cmd.rs` | Pause/resume text expansion globally, without stopping the service. |

Official CLI docs (human-readable overview of the same commands): https://espanso.org/docs/cli/

Note: the espanso website's CLI page is client-side rendered and returned no static HTML when
fetched directly, so the exact JSON schema and subcommand names were confirmed by reading the
actual Rust source referenced above rather than relying on the rendered docs page alone.

## 5. Design decisions for requirement 2a/2b (hide-then-trigger)

`Commands/TriggerMatchCommand.cs` implements the focus hand-off explicitly:

1. `Invoke()` returns `CommandResult.Hide()` immediately. `Hide` (as opposed to `Dismiss`)
   hides the Command Palette window but keeps its page/search state, matching the "hide the
   command palette window to focus [the] latest window" requirement without discarding the
   current search.
2. Actually calling `espanso match exec -t <trigger>` happens slightly later, on a
   fire-and-forget background task with a short delay (~150 ms) so that Windows has time to
   restore keyboard focus to the previously active window/control *before* espanso injects
   text — otherwise the expansion could be typed into Command Palette itself.
3. Any failure (most commonly: espanso's worker process not running) is reported through
   `ExtensionHost.LogMessage`, since the palette window is already hidden by the time the CLI
   call returns and there's no item left to show an inline error on.

This mirrors how PowerToys' own built-in "Run"/"Shell" extension invokes external processes and
dismisses the palette (`CommandResult.Dismiss()` in `RunExeItem.cs` /
`Microsoft.CmdPal.Ext.Shell`), which was used as the reference implementation for this pattern.

## 6. Building and deploying

### 6.1 Building in the cloud, without installing Visual Studio locally

GitHub Actions' `windows-latest` runners already ship with Visual Studio Build Tools, the
.NET SDK and the Windows 11 SDK preinstalled (see the runner image manifest:
https://github.com/actions/runner-images/blob/main/images/windows/Windows2022-Readme.md), so
you can get a real Windows build of this project **without installing anything on your own PC**
beyond `git` (and only if you want to push from your machine — you can also edit/commit
straight from github.com or GitHub Codespaces' web editor, which doesn't need a local Windows
machine at all for editing the source).

This repo includes `.github/workflows/build.yml`, which:

1. Checks out the code on a hosted Windows runner.
2. Restores and builds the solution with `msbuild` (via `microsoft/setup-msbuild`) for `x64`
   only (ARM64 was dropped to keep the CI matrix minimal; re-add it later if Arm support is
   needed).
3. Uploads the build output (including the generated `.msix`/`.appx` package under
   `AppxPackages\`) as a downloadable workflow artifact.

To use it:

1. Push this folder to a **GitHub repository** (public repos get GitHub Actions minutes for
   free; private repos get a limited free monthly quota too).
2. The workflow runs automatically on push/PR to `main`, or manually via the "Run workflow"
   button (Actions tab → "Build EspansoSearchBar" → "Run workflow").
3. Once green, open the run and download the `EspansoSearchBar-x64` artifact.
4. On an actual Windows 11 PC with [Developer Mode enabled](https://learn.microsoft.com/windows/apps/get-started/enable-your-device-for-development)
   and PowerToys + espanso installed, install the extracted `.msix` (double-click it, or
   `Add-AppxPackage -Path .\EspansoSearchBar_....msix` in PowerShell) and Command Palette will
   pick it up — no Visual Studio needed on that machine either.

This is genuinely a full cloud build: the actual compiler, linker, WinRT metadata generation
and MSIX packaging all run on GitHub's hosted Windows VM, not on your machine. What you can't
avoid is that *some* Windows 11 machine is eventually needed to *run/deploy* the extension
(Command Palette itself, and the WinRT COM activation, only exist on Windows) — but that
machine doesn't need Visual Studio installed, only Developer Mode + the built package.

> Other "no local install" options that do **not** work here: browser-only cloud IDEs like
> GitHub Codespaces or Gitpod default to Linux containers, and this project targets
> `net10.0-windows10.0.26100.0` with WinRT/COM interop, which cannot build on Linux. GitHub
> Actions' Windows runners are the practical free option.

### 6.2 Building locally with Visual Studio (for interactive debugging/deploying)

This extension targets the same stack as PowerToys' own Command Palette extensions
(WinAppSDK / WinUI 3, packaged MSIX, out-of-process COM). Building it requires:

1. **Visual Studio 2022** (17.10+) with the *.NET Desktop Development* and
   *Windows App SDK* / *Universal Windows Platform development* workloads, plus the
   Windows 11 SDK (10.0.26100 or newer).
2. **[PowerToys](https://github.com/microsoft/PowerToys) installed**, with Command Palette
   enabled, so the extension can be discovered once deployed.
3. **[espanso](https://github.com/espanso/espanso) installed** and its background service
   running (`espanso service start`, or just install it — the installer registers the
   service automatically).
4. Developer Mode enabled on Windows (required to sideload/deploy MSIX packages from Visual
   Studio without a store submission).

> This scaffold was produced without a local .NET/Windows App SDK toolchain available in this
> environment (`dotnet` is not installed here), so it has **not** been compiled in this
> session. Every API surface used (`CommandProvider`, `DynamicListPage`, `InvokableCommand`,
> `CommandResult`, `ExtensionHost`, the appxmanifest shape, the `Program.cs` COM bootstrap) was
> verified against the official Microsoft Learn docs and the real generator template / built-in
> extensions in the `microsoft/PowerToys` repository (see citations above and inline code
> comments). Open `EspansoSearchBar.sln` in Visual Studio, restore NuGet packages, then use
> **Build → Deploy EspansoSearchBar** (not just Build) so Windows registers the MSIX package.
> Afterwards, run the **Reload Command Palette extensions** command inside Command Palette to
> pick it up.

Steps once opened in Visual Studio:

1. Restore NuGet packages (`Microsoft.CommandPalette.Extensions`,
   `Microsoft.CommandPalette.Extensions.Toolkit`, `Shmuelie.WinRTServer`).
2. Set the platform to `x64` (the solution only defines `x64` configurations now) —
   `AnyCPU` is not supported by WinAppSDK packaged apps.
3. **Build → Deploy EspansoSearchBar**.
4. In Command Palette, run `Reload` → **Reload Command Palette extensions**.
5. Open Command Palette, select **Espanso Search Bar**, and start typing a trigger.

Placeholder assets (`Assets/*.png`) are included so the project deploys out of the box; replace
them with real icons before publishing (see
https://learn.microsoft.com/windows/powertoys/command-palette/publish-extension).

## 6.3 Enable/disable state and search gating

The page checks `EspansoStateStore.AssumedEnabled` before showing anything:

- If **assumed disabled**: only a warning banner + "Enable espanso" + "Toggle espanso" are
  shown — matches are not listed or searchable at all.
- If **assumed enabled**: management commands + the searchable match list are shown as normal.

This was a deliberate ask ("ak je disabled tak jednoducho to nebude vyhladavat, ponukne iba
zapnut, a az potom vyhlada"/"if disabled, don't search, just offer to enable, then search
after"), but it comes with an important, verified limitation:

- espanso has **no public CLI/IPC query for the current enabled/disabled state**. `espanso cmd
  enable|disable|toggle` (`espanso/src/cli/cmd.rs`) are fire-and-forget IPC events with no
  reply, and `espanso service status` only reports whether the process is *running*, not
  whether expansion is *enabled*.
- Worse, `espanso match exec -t <trigger>` gives **no feedback about being suppressed** either:
  internally it's converted into the exact same `MatchesDetected` event a real keystroke
  trigger produces (`espanso-engine/src/process/middleware/match_exec.rs`), and the very next
  middleware in the pipeline, `SuppressMiddleware`
  (`espanso-engine/src/process/middleware/suppress.rs`), silently turns it into a no-op if
  expansion is disabled — while the CLI call still exits with code 0, because it's sent
  asynchronously (`IPCClient::send_async` in `espanso/src/cli/match_cli/exec.rs`).

Because of that, `Espanso/EspansoStateStore.cs` only tracks what *this extension itself* last
set via its own Enable/Disable/Toggle commands (defaulting to "enabled"). It will drift out of
sync if the user disables espanso another way (tray icon, the default Alt+Shift+X hotkey,
another tool) — in that case, use the "Toggle espanso" item to resync. A future improvement
could poll `espanso log` for the most recent enable/disable line as a heuristic, but that
depends on log text format and isn't a stable public API, so it was intentionally left out of
this scaffold.

## 7. Possible follow-ups (not implemented yet)

- A `FallbackCommandItem` so typing a trigger directly into Command Palette's *global* search
  (without opening this extension's page first) also offers to trigger it — same pattern as
  `Microsoft.CmdPal.Ext.Shell.FallbackExecuteItem` in PowerToys.
- Reading espanso's config directly (instead of only `match list -j`) to group matches by the
  `.yml` file they came from.
- A settings page to configure the focus hand-off delay, for users on slower machines.
