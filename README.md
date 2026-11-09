<p align="center">
  <img src="branding/kauri-icon-256.png" width="112" alt="Kauri Clip icon">
</p>

<h1 align="center">Kauri Clip</h1>

<p align="center">Clipboard history and snippets for Windows and macOS, with end-to-end encrypted sync between them.</p>

Kauri Clip keeps what you copy and the text you reuse, and pastes either from a menu that opens at a shortcut. It is a port of [Clipy](https://github.com/Clipy/Clipy) to Windows, and a fork of Clipy on macOS.

## Install

| Platform | Requirement | Download |
|---|---|---|
| Windows | Windows 10 or 11, x64 | `Kauri.exe` from the `Kauri-win-x64` artifact of the [windows workflow](https://github.com/VizzleTF/kauri/actions/workflows/windows.yml) |
| macOS | macOS 13 or later | `Kauri-macOS.zip` from the `Kauri-macOS` artifact of the [macos workflow](https://github.com/VizzleTF/kauri/actions/workflows/macos.yml) |

`Kauri.exe` is one self-contained file. The macOS build is ad-hoc signed: open it the first time with right-click → **Open**, then allow it under **Privacy & Security → Accessibility**.

## First use

Copy two pieces of text, then press **Ctrl+Shift+V** on Windows or **Shift+Cmd+V** on a Mac. The menu opens with both items numbered; choose one and Kauri Clip pastes it.

To sync, run a [kauri-sync](https://github.com/VizzleTF/kauri-sync) server. Then choose **Settings → Sync → Start Sync** on one device and **Join with a Pairing Code** on the others.

## Links

| Page | What it covers |
|---|---|
| [windows/](windows) | Build and layout of the Windows app |
| [macos/](macos) | Build, tests and changes from Clipy |
| [docs/sync.md](docs/sync.md) | Sync protocol v1, with [test vectors](docs/sync-vectors) |
| [kauri-sync](https://github.com/VizzleTF/kauri-sync) | The sync server |
| [PRIVACY.md](PRIVACY.md) | What is stored where, and what the server sees |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Licenses of Clipy, ClipMenu and the libraries |

Kauri Clip is under the [MIT License](LICENSE). Clipy's authors ask that derived work not be called Clipy or ClipMenu, so this project has its own name.
