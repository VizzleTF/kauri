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
| `Kauri/UI` | Menu flyouts, settings window, snippet editor, dialogs, tray icon |
| `Kauri/DevTools.cs` | `Kauri.exe --render <screen>` draws a screen to PNG for UI review. `--render selftest` opens the real windows and snapshots them |
| `tools/render.sh` | Renders every screen in light and dark |
| `tools/make_icons.py` | Builds the `.ico` and tray glyphs from the artwork in `branding/` |

To render screens without touching your own history, set `KAURI_DATA_DIR` to another folder.
