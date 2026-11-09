# Privacy

Kauri Clip stores everything on your PC and sends nothing over the network.

## What is stored

| Data | Where |
|------|-------|
| Clipboard history: text, rich text, HTML, file paths, images and their thumbnails | `%APPDATA%\Kauri\kauri.db` |
| Snippets | `%APPDATA%\Kauri\kauri.db` |
| Settings and shortcuts | `%APPDATA%\Kauri\settings.json` |

Only the current Windows user can read these files. Kauri Clip doesn't encrypt them.

## What is not stored

- Content from apps you add under **Settings → Exclude**.
- Content that apps mark as confidential, if **Do not save content marked as confidential** is on under **Settings → Type**. Password managers use these markers.
- Clipboard types you turn off under **Settings → Type**.

## Network

Kauri Clip contains no analytics, crash reporting, telemetry or update checks. The app reaches the internet only when you click a link under **Settings → About**, and then your browser opens that link.

## Removing your data

Choose **Clear History** in the menu to delete the history. To remove everything, quit Kauri Clip and delete the `%APPDATA%\Kauri` folder.
