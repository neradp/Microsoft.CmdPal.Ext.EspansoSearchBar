# AGENTS.md — instructions for AI agents working on this repository

## What this project is

**EspansoSearchBar** is a [PowerToys Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview)
extension that replaces [espanso](https://espanso.org)'s built-in search bar
(`espanso cmd search`). It lets the user browse, filter and trigger espanso text-expansion
matches from inside Command Palette, and exposes a few espanso self-management commands
(restart service, enable/disable/toggle expansion, reload match list).

See `README.md` for the full architecture, design decisions and build instructions.

## Functional requirements

1. **List espanso matches** in a searchable list page, plus management commands for espanso
   itself (service restart, enable/disable/toggle, reload).
2. **Trigger the selected match**, with two hard constraints:
   - a. **Hide the Command Palette window first**, so keyboard focus returns to the window and
     cursor position the user was working in — espanso injects text into the currently
     focused control, and triggering while the palette still has focus would type the
     expansion into the palette itself.
   - b. **Drive the real espanso CLI** (`espanso match exec -t <trigger>`); never reimplement
     espanso's expansion/IPC logic inside the extension.
3. If espanso expansion is believed to be **disabled**, do not list/search matches — show only
   a warning banner plus Enable/Toggle commands; resume normal search after a successful
   enable.

## Hard rules for agents

- **All code, comments, commit messages, and user-facing strings are in English.** (The
  project owner communicates in Slovak; replies to the owner may be in Slovak, but nothing
  Slovak goes into the repository files.)
- **Verify APIs against primary sources before using them.** This project was burned twice by
  invented API signatures and invented NuGet versions. The ground truth is:
  - Command Palette SDK: the real source in `microsoft/PowerToys` under
    `src/modules/cmdpal/extensionsdk/Microsoft.CommandPalette.Extensions.Toolkit/` and the
    generator template under `src/modules/cmdpal/ExtensionTemplate/`.
  - NuGet versions: the template's `Directory.Packages.props` in the same repo, cross-checked
    against nuget.org (the `Toolkit` namespace ships *inside* the
    `Microsoft.CommandPalette.Extensions` package — it is **not** a separate package; the
    `Shmuelie.WinRTServer.CsWinRT` namespace ships inside `Shmuelie.WinRTServer`).
  - espanso behavior: the Rust source at `github.com/espanso/espanso` (the docs site is
    client-side rendered and incomplete).
- **Do not copy "Copyright (c) Microsoft Corporation" headers** from PowerToys reference code.
  This project's header is: `Copyright (c) the EspansoSearchBar project contributors.` (MIT).
- **Keep the build x64-only** (workflow, solution configs) unless the owner asks for ARM64.
- No local .NET/Windows App SDK toolchain is assumed; the build is validated via the GitHub
  Actions workflow in `.github/workflows/build.yml` (`windows-latest` runner, MSBuild, MSIX).

## Verified espanso facts agents must not "re-guess"

These were confirmed by reading espanso's Rust source, not docs:

- `espanso match list -j` (`espanso/src/cli/match_cli/list.rs`) emits
  `[{"triggers": [...], "replace": "...", "label": null|"..."}]`; regex-only matches report
  the literal trigger `"(none)"` and must be filtered out.
- `espanso match exec -t <trigger>` (`exec.rs`) is **fire-and-forget** (`IPCClient::send_async`):
  exit code 0 does **not** prove the expansion happened.
- `espanso match exec -t <trigger>` triggers **backspace compensation**: the engine emits a
  `TriggerCompensationEvent` (`espanso-engine/src/process/middleware/cause.rs`) and sends one
  Backspace per trigger char (`action.rs`) before injecting the replacement — it assumes the
  trigger was physically typed. Espanso's own search bar avoids this internally
  (`search.rs` selects matches with `trigger: None`); no CLI/IPC path exposes that. Hence
  `Espanso/TriggerTextTyper.cs` types one space per trigger char (Unicode scalar count) via
  `SendInput` before calling `match exec` — spaces, not the trigger itself, so the typed text
  can never re-trigger a match regardless of espanso's detector configuration.
- Espanso's Windows detector (`espanso-detect/src/win32`) uses Raw Input and, by default
  (`win32_exclude_orphan_events: true`), **drops all keyboard events with a NULL `hDevice`** —
  i.e. anything generated via `SendInput`, from any process. So text typed programmatically by
  the extension is never matched/expanded by espanso, while target apps still receive it.
- There is **no CLI/IPC query for the current enabled/disabled state**. `espanso cmd
  enable|disable|toggle` (`cli/cmd.rs`) are one-way events; `espanso service status` only says
  whether the worker *process* runs.
- When expansion is disabled, a `match exec` request is silently dropped by
  `SuppressMiddleware` (`espanso-engine/src/process/middleware/suppress.rs`) with no error
  signal — hence `Espanso/EspansoStateStore.cs` tracks a *best-effort assumed* state that can
  go stale if espanso is toggled externally (tray icon, Alt+Shift+X).
- The Windows installer (`scripts/resources/windows/setupscript.iss`) never ships an
  `espanso.exe`: the real binary is **`espansod.exe`**, and `espanso.cmd` is a one-line shim
  (`@"%~dp0espansod.exe" %*`). Install dir is Inno `{autopf}\Espanso`
  (`%LOCALAPPDATA%\Programs\Espanso` per-user, `%ProgramFiles%\Espanso` admin).
- `espanso env-path register` writes the install dir only to the **user PATH in the registry**
  (`HKCU\Environment\Path`, `espanso/src/path/win.rs`). An MSIX-activated COM server does not
  inherit that, so `Espanso/EspansoCliRunner.cs` reads `HKCU\Environment` itself and probes
  the default install folders, preferring `espansod.exe`.
- `espanso service status` exit codes (`espanso/src/exit_code.rs` + `cli/service/mod.rs`):
  0 = running, 4 = `SERVICE_NOT_RUNNING`.

## Layout quick reference

```
EspansoSearchBar/
├── Program.cs                       COM server bootstrap (mirrors the official template)
├── EspansoSearchBarExtension.cs     IExtension; GUID must match Package.appxmanifest
├── EspansoSearchBarCommandsProvider.cs  Two top-level items + Settings wiring
├── SettingsManager.cs               Settings: espanso enabled toggle + executable path
├── Espanso/                         CLI integration (runner, client, JSON model, state store)
├── Pages/EspansoSearchBarPage.cs    DynamicListPage: search + disabled gating + reload item
├── Pages/EspansoStatusPage.cs       Live 'service status' check + restart
└── Commands/                        Trigger / copy / service / refresh commands
```
