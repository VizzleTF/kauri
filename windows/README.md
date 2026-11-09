# Kauri Clip for Windows

This folder holds the Windows app: `Kauri.exe`, built with WPF on .NET 10. It keeps Clipy's menu, settings and snippet editor, and draws them in the Windows 11 style. It uses Segoe UI Variable, Fluent icons, native title bars and your accent colour, and switches between light and dark with Windows.

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
| `tools/make_icons.py` | Builds the `.ico` and tray glyphs from the artwork in `branding/` |

To render screens without touching your own history, set `KAURI_DATA_DIR` to another folder.

## Sync

**Settings → Sync** syncs snippets and folders, and text history if you turn it on, with other PCs and Macs through a [kauri-sync](https://github.com/VizzleTF/kauri-sync) server. Everything is encrypted on the PC first. The sync code and device keys are stored in `%APPDATA%\Kauri\sync.keys`, encrypted with DPAPI for your Windows account; the sync state is in `sync.json`.

`Kauri.exe --sync-check docs/sync-vectors/v1.json --out result.txt` checks the crypto against the protocol test vectors, and the merge and ordering rules. CI runs it.
