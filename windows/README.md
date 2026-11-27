<p align="center">
  <img src="../branding/kauri-icon-256.png" width="96" alt="Kauri Clip icon">
</p>

<h1 align="center">Kauri Clip for Windows</h1>

<p align="center">The Windows 10 and 11 app, built with WPF on .NET 10.</p>

`Kauri.exe` keeps Clipy's menu, settings and snippet editor, and draws them in the Windows 11 style. It uses Segoe UI Variable, Fluent icons, native title bars and your accent colour, and switches between light and dark with Windows.

## Run

`Kauri.exe` is a self-contained single file, so you don't need to install .NET. History and settings are stored in `%APPDATA%\Kauri`.

## Build

You need the .NET 10 SDK.

```bash
dotnet publish windows/Kauri -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

## Layout

| Path | What is there |
|---|---|
| `Kauri/Core` | History database (SQLite), clipboard monitoring, paste, global shortcuts, settings, login item |
| `Kauri/UI` | Menu flyouts, settings window, snippet editor, dialogs, tray icon, the Sync pane |
| `Kauri/Sync` | Kauri Sync ([docs/sync.md](../docs/sync.md)): crypto on libsodium and .NET, the HTTP client, the sync engine |
| `Kauri/DevTools.cs` | `Kauri.exe --render <screen>` draws a screen to PNG for UI review. `--render selftest` opens the real windows and snapshots them |
| `tools/render.sh` | Renders every screen in light and dark |
| `tools/hotkey-test.ps1` | Presses the menu shortcut over Notepad or Edge and checks that the menu stays open, takes keys and pastes |
| `tools/make_icons.py` | Builds the `.ico` and tray glyphs from the artwork in `branding/` |

To render screens without touching your own history, set `KAURI_DATA_DIR` to another folder.

## Troubleshooting

The menu opens with **Ctrl+Shift+V**. Snippets open with **Ctrl+Shift+B**. Change either under **Settings → Shortcuts**.

**The shortcut does nothing.** Another app may hold it. Windows then refuses it, and Kauri Clip shows a tray notification such as "Ctrl+Shift+V is taken by another app"; the tray icon's tooltip keeps saying so. Pick another shortcut under **Settings → Shortcuts**.

**The menu flashes and closes, or you need a log to send.** Quit Kauri Clip from its tray menu, then start it from PowerShell with a trace file:

```powershell
$env:KAURI_TRACE = "$env:TEMP\kauri-trace.txt"
& "C:\path\to\Kauri.exe"
```

Press the shortcut, then attach `%TEMP%\kauri-trace.txt`. The trace is appended to and records each shortcut press, refused shortcuts, which app was in front, and when and why the menu closed.

**The app you were typing in keeps its focus while the menu is open.** This is intended. The menu never takes the activation, so Windows cannot refuse it and paste goes straight back to that app. While the menu is open it reads keys and mouse clicks through low-level hooks, as a system menu does. It takes only the keys it uses: arrows, Enter, Space, Esc, digits and letters. Modifiers, Caps Lock, media keys and Kauri's own shortcuts pass through. Alt+Tab, Win+D and other Alt or Win combinations close the menu and then work as usual, and so does any other window coming to the front.

Over an app running as administrator, Windows does not pass its input to Kauri's hooks, so there the menu takes the activation instead. Auto-paste into such an app needs Kauri Clip to run as administrator too.

**Check the shortcut on your own PC.** From the repository root, after the build above:

```powershell
pwsh -ExecutionPolicy Bypass -File windows\tools\hotkey-test.ps1 -Exe dist\Kauri.exe -Target notepad
```

`-Target edge` uses Microsoft Edge instead of Notepad. Quit your own Kauri Clip first: the test starts `dist\Kauri.exe` with an empty data folder and presses keys and clicks for about 30 seconds. It prints one line per round and ends with `0 failed check(s)`. The [windows-hotkey](../.github/workflows/windows-hotkey.yml) workflow runs it on every pull request.

## Sync

**Settings → Sync** syncs snippets and folders, and text history if you turn it on, with other PCs and Macs through a [kauri-sync](https://github.com/VizzleTF/kauri-sync) server. Everything is encrypted on the PC first. The sync code and device keys are stored in `%APPDATA%\Kauri\sync.keys`, encrypted with DPAPI for your Windows account; the sync state is in `sync.json`.

`Kauri.exe --sync-check docs/sync-vectors/v1.json --out result.txt` checks the crypto against the protocol test vectors, and the merge and ordering rules. CI runs it.
