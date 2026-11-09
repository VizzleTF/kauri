# Kauri Clip

<img src="branding/kauri-icon-256.png" width="96" alt="Kauri Clip icon">

Kauri Clip keeps your clipboard history and saved snippets, and pastes them from a menu that you open with a shortcut. It is a port of [Clipy](https://github.com/Clipy/Clipy) to Windows and back to macOS, with end-to-end encrypted sync between your devices.

This repository holds every platform:

| Folder | What it is | Status |
|---|---|---|
| [`windows/`](windows) | Windows 10 and 11 app (WPF, .NET 10) | Works, with sync |
| [`macos/`](macos) | macOS app (Swift, a fork of Clipy 1.4) | Works, with sync |
| [`docs/sync.md`](docs/sync.md) | Kauri Sync protocol and test vectors, shared by all clients | v1, server implemented |
| [`branding/`](branding) | App and tray icons | |

The sync server lives in its own repository, [kauri-sync](https://github.com/VizzleTF/kauri-sync).

## Features

- **Clipboard history:** grouped into folders of 10, numbered, with tooltips, image thumbnails and colour previews. Sorted by last use or by date.
- **Snippets:** folders of reusable text. Each folder can have its own shortcut. You can drag and drop them and import or export them as XML compatible with Clipy.
- **Shortcuts:**

  | Action | Windows | Mac |
  |---|---|---|
  | Main menu | Ctrl+Shift+V | Shift+Cmd+V |
  | History | Ctrl+Win+V | Control+Cmd+V |
  | Snippets | Ctrl+Shift+B | Shift+Cmd+B |

  On top of that: paste as plain text, delete from history, excluded apps, and skipping content that apps mark as confidential, such as passwords.
- **Sync:** snippets, and if you turn it on, text history, between Windows and macOS.
  - Encrypted on the device, so the server cannot read it.
  - No account: a 24-word sync code, or a 12-character pairing code from a device that is already in, confirmed by a 4-digit number on both screens.
  - Updates arrive within a second.
  - Removing a lost device re-keys everything. See [docs/sync.md](docs/sync.md).

## Download

| Platform | Get it |
|---|---|
| Windows (x64) | Build from [`windows/`](windows), or take `Kauri.exe` from the Actions artifacts |
| macOS 13+ | Build from [`macos/`](macos), or take `Kauri-macOS.zip` from the Actions artifacts (ad-hoc signed) |

## License and credits

Kauri Clip is released under the [MIT License](LICENSE). It is based on [Clipy](https://github.com/Clipy/Clipy) (MIT, © Clipy Project), which in turn is based on ClipMenu (MIT, © Naotaka Morimoto). Clipy's authors ask that derived work not use the names "Clipy" or "ClipMenu", which is why this project has its own name. For the full license texts, see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). For what is stored where, see [PRIVACY.md](PRIVACY.md).
