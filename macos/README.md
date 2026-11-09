# Kauri Clip for macOS

**Planned.** Nothing has been built here yet. This page records the plan.

The macOS app will start from [Clipy](https://github.com/Clipy/Clipy) 1.4, imported with its git history so that authorship is preserved. It will then be changed in these ways:

- **Name.** Renamed to Kauri Clip with the bundle ID `io.github.vizzletf.kauri`. Data goes in `~/Library/Application Support/Kauri`.
- **Removed.** Firebase analytics and crash reporting are taken out. Sparkle stays only if it points at this repository's releases.
- **Sync added.** Snippets and history sync with the Windows app through `kauri-sync`, following [docs/sync.md](../docs/sync.md). The app gets a sync settings pane, pairing by a 6-digit code, and a device list.
- **Icons.** Shared with Windows, from `branding/`.

Builds will target macOS 14 and later on Apple Silicon and Intel. ML-KEM envelopes need macOS 26 or later; on older systems the app falls back to X25519-only envelopes and says so in the device list.
