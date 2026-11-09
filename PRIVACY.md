# Privacy

Kauri Clip keeps your clipboard history and snippets on your device. It sends nothing over the network unless you turn on sync.

## What is stored

| Data | Windows | macOS |
|------|---------|-------|
| Clipboard history: text, rich text, HTML, file paths, images and their thumbnails | `%APPDATA%\Kauri\kauri.db` | `~/Library/Application Support/io.github.vizzletf.kauri/sqlite.db` |
| Snippets | same file | same file |
| Settings and shortcuts | `%APPDATA%\Kauri\settings.json` | the app's user defaults |
| Sync state, if sync is on | `%APPDATA%\Kauri\sync.json` | `sync.json` next to the database |
| Sync code and device keys, if sync is on | `%APPDATA%\Kauri\sync.keys`, encrypted with DPAPI | the Keychain, this Mac only |

Only your user account can read these files. The history and snippets are not encrypted on disk.

## What is not stored

- Content from apps you add under **Settings → Exclude**.
- Content that apps mark as confidential, if **Do not save content marked as confidential** is on under **Settings → Type**. Password managers use these markers.
- Clipboard types you turn off under **Settings → Type**.

## Sync

Sync is off until you turn it on under **Settings → Sync**. It then talks only to the server you enter.

- Snippets and folders sync. Clipboard history syncs only if you turn it on, and then only text, at most 500 items.
- Everything is encrypted on your device before it is sent. The server stores ciphertext, padded to fixed sizes, and cannot read titles, contents or device names.
- The server does see your IP address, when you sync, how many records you have and their padded sizes.
- Anyone with your 24-word sync code can read your synced data, so keep it safe. Removing a device moves your other devices to a new sync code.

The protocol is described in [docs/sync.md](docs/sync.md).

## Network

Kauri Clip contains no analytics, crash reporting, telemetry or update checks. Apart from sync, the app reaches the internet only when you click a link under **Settings → About**, and then your browser opens that link.

## Removing your data

Choose **Clear History** in the menu to delete the history. To remove everything, turn off sync, quit Kauri Clip and delete its data folder. Turning off sync deletes the sync keys: `sync.keys` on Windows, the Keychain item "Kauri Clip sync keys" on a Mac.
