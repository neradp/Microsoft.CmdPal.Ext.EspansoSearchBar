# Espanso Search Bar — a PowerToys Command Palette extension

This project replaces espanso's built-in search bar (`espanso cmd search`) with a
[PowerToys Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview)
extension, so that browsing, filtering and triggering
[espanso](https://espanso.org) text-expansion matches happens inside the same launcher
Windows power users already use for everything else.

## Goals

- **List espanso matches** in a searchable page; espanso self-management lives on its own
  surfaces: a live **Espanso Status** page (service running check + restart + stateless
  enable/disable/toggle of automatic expansion) and the extension **settings page**
  (executable path override + one-shot toggle action). The search page itself keeps only the
  "Reload match list" helper.
- **Let the user execute ("trigger") the selected match**:
  - Hide the Command Palette window first, so focus returns to whatever window/cursor
    the user was working in — espanso injects text into the currently focused control.
  - Trigger the match from the command line (`espanso match exec -t <trigger>`), i.e.
    don't reimplement espanso's expansion logic — just drive the real espanso CLI/IPC.
- **Matches stay searchable and manually triggerable even while espanso's automatic expansion
  is disabled** — disabling only affects keyboard-typed triggers, not `match exec` (see
  "Enable/disable actions" below).

## 2. How it works (architecture)

```
┌─────────────────────────┐   COM/WinRT (out-of-process)   ┌───────────────────────────┐
│   PowerToys Command      │ ───────────────────────────── │  EspansoSearchBar.exe      │
│   Palette (host process) │ ◄───────────────────────────  │  (this extension)          │
└─────────────────────────┘                                 └─────────────┬─────────────┘
                                                                            │ Process.Start
                                                                            ▼
                                                              ┌───────────────────────────┐
                                                              │  espansod.exe (CLI)        │
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
the real `espanso` executable (see Goals above). This keeps the extension small, and guarantees
behavior stays in sync with
whatever espanso version the user has installed, since we're using its public, documented CLI:
https://espanso.org/docs/cli/

## 3. Project layout

```
EspansoSearchBar.sln
EspansoSearchBar/
├── Program.cs                          Process entry point / COM server bootstrap
├── EspansoSearchBarExtension.cs        IExtension implementation (COM root object)
├── EspansoSearchBarCommandsProvider.cs Top-level commands ("Espanso Search Bar", "Espanso Status")
├── SettingsManager.cs                  Extension settings: enable toggle + executable path
├── Package.appxmanifest                MSIX manifest: COM registration + CmdPalProvider
├── app.manifest                        Win32 app manifest (DPI awareness, OS compat)
├── Espanso/
│   ├── EspansoMatch.cs                 JSON model for "espanso match list -j" entries
│   ├── EspansoCliRunner.cs             Hidden-process runner + espansod.exe discovery
│   └── EspansoClient.cs                High-level espanso operations used by the UI
├── Pages/
│   ├── EspansoSearchBarPage.cs         DynamicListPage: searchable match list
│   └── EspansoStatusPage.cs            Live service status ("service status") + restart
└── Commands/
    ├── TriggerMatchCommand.cs          Hides palette, types padding, runs "match exec -t <trigger>"
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

### Finding the espanso executable

The official Windows installer (`scripts/resources/windows/setupscript.iss` in the espanso
repository) never installs an `espanso.exe`: the real binary is **`espansod.exe`**, and
`espanso.cmd` is just a one-line shim (`@"%~dp0espansod.exe" %*`). On top of that,
`espanso env-path register` writes the install directory only into the *user* PATH in the
registry (`HKCU\Environment\Path`, see `espanso/src/path/win.rs`) — which an MSIX-activated
COM server like this extension does not inherit. `Espanso/EspansoCliRunner.cs` therefore
resolves the executable itself, in order:

1. the user-configured path from the extension settings (file or folder),
2. every directory on the process PATH **and** the user PATH read from `HKCU\Environment`,
3. the installer defaults `%LOCALAPPDATA%\Programs\Espanso` and `%ProgramFiles%\Espanso`,

preferring `espansod.exe` over `espanso.exe` in each location.

Official CLI docs (human-readable overview of the same commands): https://espanso.org/docs/cli/

Note: the espanso website's CLI page is client-side rendered and returned no static HTML when
fetched directly, so the exact JSON schema and subcommand names were confirmed by reading the
actual Rust source referenced above rather than relying on the rendered docs page alone.

## 5. Design decisions: hide the palette first, then trigger via the CLI

`Commands/TriggerMatchCommand.cs` implements the focus hand-off explicitly:

1. `Invoke()` returns `CommandResult.Hide()` immediately. `Hide` (as opposed to `Dismiss`)
   hides the Command Palette window but keeps its page/search state — focus returns to the
   window the user was previously working in, without discarding the current search.
2. Actually calling `espanso match exec -t <trigger>` happens slightly later, on a
   fire-and-forget background task with a short delay (~150 ms) so that Windows has time to
   restore keyboard focus to the previously active window/control *before* espanso injects
   text — otherwise the expansion could be typed into Command Palette itself.
3. **Trigger compensation:** `match exec -t <trigger>` makes the espanso engine emit a
   `TriggerCompensationEvent` (`espanso-engine/src/process/middleware/cause.rs`), which sends
   one Backspace per trigger character (`action.rs`: `trigger.chars().count()`) before
   injecting the replacement — espanso assumes the trigger was physically typed in the target
   window and deletes it. Since nothing was typed when we invoke the CLI, those backspaces
   would destroy the user's existing text. (Espanso's own search bar avoids this internally by
   selecting matches with `trigger: None` — `search.rs` — but that path isn't reachable via
   CLI/IPC.) The fix in `Espanso/TriggerTextTyper.cs`: after the focus hand-off, the extension
   types *padding* — one space per trigger character — into the focused window via `SendInput`
   (`KEYEVENTF_UNICODE`), then calls `match exec`. The backspace compensation deletes exactly
   that padding. Spaces are used instead of the trigger text itself so that nothing the
   extension types can ever be matched as a trigger — even on setups with
   `win32_exclude_orphan_events: false`, where espanso's Raw Input detector *would* see
   `SendInput`-generated keystrokes and could expand the match a second time.
4. Any failure (most commonly: espanso's worker process not running) is reported through
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
3. Generates a throwaway **self-signed certificate** (`CN=EspansoSearchBar`, matching the
   manifest Publisher) and signs the MSIX with it. Signing is mandatory: Windows refuses to
   install *unsigned* packages that contain executable activations (error `0x80073D2B`),
   and this extension is an exe activated as an out-of-process COM server.
4. Uploads two artifacts: `EspansoSearchBar-msix-x64` (the signed `.msix` + the public
   `EspansoSearchBar.cer`) and `EspansoSearchBar-x64` (the raw build output).

To use it:

1. Push this folder to a **GitHub repository** (public repos get GitHub Actions minutes for
   free; private repos get a limited free monthly quota too).
2. The workflow runs automatically on push/PR to `main`, or manually via the "Run workflow"
   button (Actions tab → "Build EspansoSearchBar" → "Run workflow").
3. Once green, open the run and download the `EspansoSearchBar-msix-x64` artifact; extract it.
4. On the target Windows 11 PC (with PowerToys + espanso installed), first trust the CI
   signing certificate — **one-time step, requires an elevated (admin) PowerShell**:

   ```powershell
   Import-Certificate -FilePath .\EspansoSearchBar.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```

5. Then install the package (regular, non-elevated PowerShell is fine):

   ```powershell
   Add-AppxPackage -Path .\EspansoSearchBar_0.0.42.0_x64.msix
   ```

   CI stamps every run with a unique, increasing version (`0.0.<run number>.0`), so a package
   from a newer run always installs cleanly as an update over the previous one. If you ever
   need to *reinstall the exact same version* (Windows rejects that by default), add the
   force-update flags:

   ```powershell
   Add-AppxPackage -Path .\EspansoSearchBar_0.0.42.0_x64.msix -ForceUpdateFromAnyVersion -ForceApplicationShutdown
   ```

   `-ForceUpdateFromAnyVersion` allows same-version (and downgrade) installs;
   `-ForceApplicationShutdown` kills the running COM-server process so its files aren't locked.

   Command Palette will pick it up (run **Reload Command Palette extensions** inside the
   palette if it doesn't appear immediately) — no Visual Studio needed on that machine either.

> Because each CI run generates a *new* self-signed certificate, re-import the fresh `.cer`
> whenever you install a package from a newer run. For real distribution, replace this with a
> proper code-signing certificate (and remove the cert-generation step from the workflow).

This is genuinely a full cloud build: the actual compiler, linker, WinRT metadata generation
and MSIX packaging all run on GitHub's hosted Windows VM, not on your machine. What you can't
avoid is that *some* Windows 11 machine is eventually needed to *run/deploy* the extension
(Command Palette itself, and the WinRT COM activation, only exist on Windows) — but that
machine doesn't need Visual Studio installed, only the built package + the imported
certificate.

### 6.2 Creating a release

Pushing a git tag of the form `v<major>.<minor>.<patch>` publishes a **GitHub Release**
automatically:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The same `build.yml` workflow then:

1. Stamps the package version from the tag (`v0.1.0` → MSIX version `0.1.0.0`) instead of the
   `0.0.<run number>.0` scheme used for ordinary CI builds.
2. Builds and signs the MSIX as usual (with a fresh CI self-signed certificate).
3. Creates a GitHub Release named after the tag, attaching the signed `.msix` and the public
   `EspansoSearchBar.cer`, with installation instructions in the release notes (same
   trust-certificate + `Add-AppxPackage` steps as above).

Users then install straight from the Releases page — no need to dig artifacts out of workflow
runs. Remember that each release ships a *new* self-signed certificate, so the `.cer` from that
release must be re-imported before installing (see the note above about real code-signing
certificates for proper distribution).

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

The `Assets/*.png` icons are simple generated graphics (teal rounded square with a white ":e"
monogram — colon as the typical espanso trigger prefix — and a yellow text caret). They are
original artwork for this project, intentionally not derived from the espanso logo.

### 6.3 Enable/disable actions and why nothing is gated on them

Matches are always listed, searchable and manually triggerable — even while espanso's
automatic expansion is disabled. This is intentional and verified against espanso's source:

- The runtime toggle (`espanso cmd enable|disable|toggle`, also the Alt+Shift+X hotkey) is
  implemented by `DisableMiddleware` (`espanso-engine/src/process/middleware/disable.rs`),
  which **only blocks keyboard events** while disabled. A `match exec -t <trigger>` IPC
  request is not a keyboard event and still expands normally — so manually triggering
  matches from this extension with automatic expansion switched off is a perfectly valid
  (and sometimes preferable) workflow.
- `SuppressMiddleware` (`suppress.rs`) is a different mechanism: it drops `MatchesDetected`
  events only when the active *config file* has `enable: false` — it does not react to the
  runtime toggle.
- espanso has **no public CLI/IPC query for the current enabled/disabled state**: `espanso cmd
  enable|disable|toggle` (`espanso/src/cli/cmd.rs`) are fire-and-forget IPC events with no
  reply, and `espanso service status` only reports whether the process is *running*.

Because the real state cannot be queried, the extension never renders an on/off switch that
claims to know it. Instead, the **Espanso Status** page offers stateless one-shot actions —
"Toggle/Enable/Disable automatic expansion" — and the settings page has a one-shot "Toggle
automatic expansion now" checkbox that runs `espanso cmd toggle` once when saved checked and
immediately resets itself (its value is never persisted).

## 7. Possible follow-ups (not implemented yet)

- A `FallbackCommandItem` so typing a trigger directly into Command Palette's *global* search
  (without opening this extension's page first) also offers to trigger it — same pattern as
  `Microsoft.CmdPal.Ext.Shell.FallbackExecuteItem` in PowerToys.
- Reading espanso's config directly (instead of only `match list -j`) to group matches by the
  `.yml` file they came from.
- A settings page to configure the focus hand-off delay, for users on slower machines.

## Credits

This project was created with the help of AI.

Co-authored-by: [GitHub Copilot](https://github.com/features/copilot) — design, implementation,
CI setup and documentation were developed collaboratively with GitHub Copilot CLI, with all
espanso and PowerToys Command Palette APIs verified against their upstream sources.
