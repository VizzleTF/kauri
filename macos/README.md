<p align="center">
  <img src="../branding/kauri-icon-256.png" width="96" alt="Kauri Clip icon">
</p>

<h1 align="center">Kauri Clip for macOS</h1>

<p align="center">The macOS app: a fork of Clipy 1.4.0 with Kauri Sync, for macOS 13 or later.</p>

## What differs from Clipy

- **Name and identity.** The app is Kauri Clip (`Kauri.app`), bundle ID `io.github.vizzletf.kauri`, with the icons from [`branding/`](../branding). Its data lives in `~/Library/Application Support/io.github.vizzletf.kauri`, apart from Clipy's.
- **Removed.** Firebase analytics and crash reporting, the Sparkle updater (its feed and key were Clipy's), and the Donate pane.
- **Added: Sync.** A Sync pane in Settings syncs snippets and folders, and clipboard history if you turn it on, with the Windows app and other Macs through a [kauri-sync](https://github.com/VizzleTF/kauri-sync) server. Everything is encrypted on the Mac first; see [docs/sync.md](../docs/sync.md).
- **Added: About pane** with the version and links to this project and to Clipy.

The Swift module is still called `Clipy`, and so are the project, the scheme and the folders, so a new Clipy release merges without renames.

## Build

1. Open `Clipy.xcodeproj` in Xcode 26.
2. Build the `Clipy` scheme. It is ad-hoc signed by default.

macOS ties the Accessibility permission to the code signature, so with ad-hoc signing it asks again after every build. To avoid that, set your own Developer ID in `Configurations/CodeSigning.xcconfig`.

## Test

```bash
xcodebuild -project Clipy.xcodeproj -scheme Clipy -skipPackagePluginValidation -skipMacroValidation test
```

The test target builds with Xcode 26.5, as in CI. Xcode 27 fails with "Multiple commands produce IssueReporting…", because two packages export an `IssueReporting` framework.

The sync code also has a check that needs only the Swift toolchain. It runs the test vectors from [`docs/sync-vectors/v1.json`](../docs/sync-vectors/v1.json) and the merge and ordering rules:

```bash
sh scripts/sync_check.sh
```

## Sync code

| File | What it does |
|---|---|
| `Clipy/Sources/Sync/KauriCrypto.swift` | Keys, XChaCha20-Poly1305, envelopes, signed requests, the BIP-39 sync code |
| `Clipy/Sources/Sync/KauriCurve.swift`, `KauriPairing.swift` | SPAKE2 pairing over edwards25519 (a TweetNaCl port) |
| `Clipy/Sources/Sync/KauriSyncModel.swift` | Records, hybrid logical clocks, order keys, state and Keychain storage |
| `Clipy/Sources/Sync/KauriSyncClient.swift` | The HTTP API and Server-Sent Events |
| `Clipy/Sources/Sync/KauriSync.swift` | Compares the database with the records, merges, pushes and pulls |
| `Clipy/Sources/Sync/SyncSettingsView.swift` | The Sync pane |

Sync uses CryptoKit only and adds no package. The chain secret and the device keys are kept in the Keychain, and the sync state in `sync.json` next to the database.

`scripts/` holds the one-off scripts that turned Clipy into Kauri Clip (`rebrand.py`, `add_sync_to_project.py`, `kauri_strings.py`, `pbxproj_remove.py`), so the same steps can be repeated after merging a new Clipy release.

## License

MIT, like Clipy and ClipMenu. See [LICENSE](LICENSE), [LICENSE_CLIPMENU](LICENSE_CLIPMENU) and [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md). Clipy's authors ask that derived work not be called Clipy or ClipMenu, which is why this one is Kauri Clip.
