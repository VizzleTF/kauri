import SwiftUI

struct AboutSettingsView: View {
    @Environment(\.openURL)
    private var openURL

    var body: some View {
        SettingsGrid {
            SettingsSection(title: .Settings.version, bottomDivider: true) {
                Text(verbatim: Bundle.main.appVersion.map { "Kauri Clip v\($0)" } ?? "Kauri Clip")
                    .textSelection(.enabled)
                Button(.Settings.releases) {
                    openURL(URL(string: "https://github.com/VizzleTF/kauri/releases")!)
                }
            }

            SettingsSection(title: .Settings.basedOn) {
                Text(.Settings.basedOnClipyDescription)
                    .settingDescription()
                Button(.Settings.clipyOnGitHub) {
                    openURL(URL(string: "https://github.com/Clipy/Clipy")!)
                }
            }
        }
    }
}
