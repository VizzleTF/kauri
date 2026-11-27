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
| `tools/hotkey-test.ps1` | Presses the menu shortcut over another app and checks the menu stays open, takes keys and pastes |
| `tools/make_icons.py` | Builds the `.ico` and tray glyphs from the artwork in `branding/` |

To render screens without touching your own history, set `KAURI_DATA_DIR` to another folder.

## How the menu takes input

The hot-key menu never takes the activation. The app you were in keeps the foreground and the keyboard focus, so Windows' foreground lock cannot refuse the menu and paste goes straight back to that app. While the menu is open, low-level keyboard and mouse hooks give it the keys and close it on a click outside, as a system menu does. Ctrl, Shift, Alt and Win pass through; a key pressed with Alt or Win closes the menu and goes on to Windows.

If a shortcut is taken by another app, Windows refuses it and Kauri Clip says so in a notification from the tray icon.

To see why the menu opened or closed, start `Kauri.exe` with `KAURI_TRACE` set to a file path. `tools/hotkey-test.ps1` presses the shortcut over Notepad or Edge and checks that the menu stays open, takes Escape and pastes; the `windows-hotkey` workflow runs it.

## Sync

**Settings → Sync** syncs snippets and folders, and text history if you turn it on, with other PCs and Macs through a [kauri-sync](https://github.com/VizzleTF/kauri-sync) server. Everything is encrypted on the PC first. The sync code and device keys are stored in `%APPDATA%\Kauri\sync.keys`, encrypted with DPAPI for your Windows account; the sync state is in `sync.json`.

`Kauri.exe --sync-check docs/sync-vectors/v1.json --out result.txt` checks the crypto against the protocol test vectors, and the merge and ordering rules. CI runs it.
