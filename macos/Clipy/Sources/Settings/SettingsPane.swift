import Settings
import SwiftUI

enum SettingsPane: String, CaseIterable {
    case general
    case menu
    case clipboardType
    case excludedApplications
    case shortcuts
    case sync
    case beta
    case about

    var title: String {
        switch self {
        case .general:
            String(localized: .Settings.general)
        case .menu:
            String(localized: .Settings.menu)
        case .clipboardType:
            String(localized: .Settings.paneType)
        case .excludedApplications:
            String(localized: .Settings.exclude)
        case .shortcuts:
            String(localized: .Settings.shortcuts)
        case .sync:
            String(localized: .Settings.sync)
        case .beta:
            String(localized: .Settings.beta)
        case .about:
            String(localized: .Settings.about)
        }
    }

    private var symbol: String {
        switch self {
        case .general:
            "gearshape"
        case .menu:
            "filemenu.and.selection"
        case .clipboardType:
            "doc.on.clipboard"
        case .excludedApplications:
            "nosign"
        case .shortcuts:
            "keyboard"
        case .sync:
            "arrow.triangle.2.circlepath.icloud"
        case .beta:
            "testtube.2"
        case .about:
            "info.circle"
        }
    }

    @MainActor
    func asPanelConvertible() -> some SettingsPaneConvertible {
        Settings.Pane(
            identifier: Settings.PaneIdentifier(rawValue),
            title: title,
            toolbarIcon: NSImage(systemSymbolName: symbol, accessibilityDescription: title)!
        ) {
            SettingsScrollView {
                content
            }
        }
    }

    @MainActor
    @ViewBuilder
    private var content: some View {
        switch self {
        case .general:
            GeneralSettingsView()
        case .menu:
            MenuSettingsView()
        case .clipboardType:
            ClipboardTypeSettingsView()
        case .excludedApplications:
            ExcludedApplicationsSettingsView()
        case .shortcuts:
            ShortcutSettingsView()
        case .sync:
            SyncSettingsView()
        case .beta:
            BetaSettingsView()
        case .about:
            AboutSettingsView()
        }
    }
}
