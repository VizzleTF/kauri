//
//  SyncSettingsView.swift
//
//  The Sync pane: turn sync on, add and remove devices, choose what syncs.
//

import AppKit
import SwiftUI

struct SyncSettingsView: View {
    @ObservedObject
    private var sync = KauriSync.shared

    @State
    private var server = KauriSync.shared.state.server
    @State
    private var sheet: Sheet?
    @State
    private var error: String?
    @State
    private var busy = false

    enum Sheet: Identifiable {
        case syncCode(String)
        case enterCode
        case enterWords
        case pairing
        case confirmRemove(KauriSync.DeviceRow)
        case confirmTurnOff

        var id: String {
            switch self {
            case .syncCode: "code"
            case .enterCode: "enterCode"
            case .enterWords: "enterWords"
            case .pairing: "pairing"
            case .confirmRemove(let device): "remove" + device.id
            case .confirmTurnOff: "off"
            }
        }
    }

    var body: some View {
        Group {
            if sync.isOn {
                onView
            } else {
                offView
            }
        }
        .sheet(item: $sheet) { sheet in
            sheetView(sheet)
        }
        .onChange(of: sync.newSyncCode) { code in
            if let code {
                sheet = .syncCode(code)
                sync.newSyncCode = nil
            }
        }
        .alert(Text(verbatim: error ?? ""), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK") { error = nil }
        }
        .alert(Text("This Mac was removed from sync."), isPresented: $sync.wasRemoved) {
            Button("OK") {}
        }
        .task { await sync.refreshDevices() }
    }

    // MARK: - Off

    private var offView: some View {
        SettingsGrid {
            SettingsSection(title: .Settings.server, bottomDivider: true) {
                TextField(text: $server, prompt: Text(verbatim: "https://sync.example.com")) {
                    Text("Sync server URL")
                }
                .labelsHidden()
                .frame(width: 300)
                .onSubmit { sync.setServer(server) }
                Text(.Settings.syncDescription)
                    .settingDescription()
            }

            SettingsSection(title: .Settings.sync) {
                Button(.Settings.startSync) {
                    run { try await sync.createChain(server: server) }
                }
                Button(.Settings.joinWithAPairingCode) { sheet = .enterCode }
                Button(.Settings.joinWithTheSyncCode) { sheet = .enterWords }
            }
        }
        .disabled(busy)
    }

    // MARK: - On

    private var onView: some View {
        SettingsGrid {
            SettingsSection(title: .Settings.status, bottomDivider: true) {
                HStack(spacing: 6) {
                    Circle().fill(statusColor).frame(width: 8, height: 8)
                    Text(statusText)
                }
                Text(verbatim: sync.state.server)
                    .settingDescription()
                    .textSelection(.enabled)
            }

            SettingsSection(title: .Settings.whatSyncs, bottomDivider: true) {
                Toggle(.Settings.snippetsAndFolders, isOn: .constant(true))
                    .disabled(true)
                Toggle(.Settings.syncHistoryToggle, isOn: Binding(get: { sync.state.syncsHistory }, set: { sync.setSyncsHistory($0) }))
                Text(.Settings.syncHistoryDescription)
                    .settingDescription()
                    .padding(.leading, 20)
            }

            SettingsSection(title: .Settings.devices, bottomDivider: true) {
                ForEach(sync.devices) { device in
                    HStack {
                        Image(systemName: device.isThisDevice ? "laptopcomputer" : "desktopcomputer")
                            .foregroundStyle(.secondary)
                            .accessibilityHidden(true)
                        Text(verbatim: device.name)
                        if device.isThisDevice {
                            Text(.Settings.thisMac).foregroundStyle(.secondary)
                        }
                        if !device.isPostQuantum {
                            Text(.Settings.notPostQuantum).font(.caption).foregroundStyle(.orange)
                        }
                        Spacer()
                        if !device.isThisDevice {
                            Button(.Settings.removeDeviceButton) { sheet = .confirmRemove(device) }
                                .controlSize(.small)
                        }
                    }
                    .frame(width: 340)
                }
                Button(.Settings.addADevice) { sheet = .pairing }
            }

            SettingsSection {
                HStack {
                    Button(.Settings.showSyncCode) {
                        if let code = sync.syncCode { sheet = .syncCode(code) }
                    }
                    Button(.Settings.turnOffSync) { sheet = .confirmTurnOff }
                }
            }
        }
        .disabled(busy)
    }

    private var statusText: LocalizedStringResource {
        switch sync.status {
        case .off: .Settings.syncIsOff
        case .connecting: .Settings.connecting
        case .syncing: .Settings.syncing
        case .synced: .Settings.upToDate
        case .offline: .Settings.offlineWillRetry
        }
    }

    private var statusColor: Color {
        switch sync.status {
        case .synced: .green
        case .syncing, .connecting: .yellow
        case .offline: .orange
        case .off: .gray
        }
    }

    private func run(_ work: @escaping () async throws -> Void) {
        busy = true
        sync.setServer(server)
        Task {
            defer { busy = false }
            do { try await work() } catch { self.error = error.localizedDescription }
        }
    }

    // MARK: - Sheets

    @ViewBuilder
    private func sheetView(_ sheet: Sheet) -> some View {
        switch sheet {
        case .syncCode(let code):
            SyncCodeSheet(code: code) { self.sheet = nil }
        case .enterCode:
            JoinWithCodeSheet(server: server) { self.sheet = nil }
        case .enterWords:
            EntrySheet(title: "Join with the Sync Code…", prompt: "Enter the 24 words", multiline: true) { words in
                self.sheet = nil
                if let words { run { try await sync.join(server: server, words: words) } }
            }
        case .pairing:
            AddDeviceSheet { self.sheet = nil }
        case .confirmRemove(let device):
            ConfirmSheet(title: "removeDeviceTitle \(device.name)", message: "removeDeviceMessage", action: "Remove") { confirmed in
                self.sheet = nil
                if confirmed { run { try await sync.removeDevice(id: device.id) } }
            }
        case .confirmTurnOff:
            ConfirmSheet(title: "Turn Off Sync…", message: "turnOffSyncMessage", action: "Turn Off") { confirmed in
                self.sheet = nil
                if confirmed { run { await sync.turnOff() } }
            }
        }
    }
}

// MARK: - Sheets

private struct SheetFrame<Content: View>: View {
    let title: LocalizedStringKey
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(title).font(.headline)
            content
        }
        .padding(20)
        .frame(width: 440)
    }
}

private struct SyncCodeSheet: View {
    let code: String
    let done: () -> Void

    var body: some View {
        SheetFrame(title: "Sync Code") {
            Text("syncCodeMessage").fixedSize(horizontal: false, vertical: true)
            let words = code.split(separator: " ").map(String.init)
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), alignment: .leading), count: 4), alignment: .leading, spacing: 6) {
                ForEach(words.indices, id: \.self) { i in
                    HStack(spacing: 4) {
                        Text(verbatim: "\(i + 1).").foregroundStyle(.secondary).monospacedDigit()
                        Text(verbatim: words[i]).font(.system(.body, design: .monospaced))
                    }
                }
            }
            .padding(12)
            .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .textBackgroundColor)))
            .textSelection(.enabled)
            HStack {
                Button("Copy") {
                    NSPasteboard.general.clearContents()
                    // Concealed, so clipboard managers (this one too) do not keep it.
                    let item = NSPasteboardItem()
                    item.setString(code, forType: .string)
                    item.setString("", forType: NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"))
                    NSPasteboard.general.writeObjects([item])
                }
                Spacer()
                Button("Done", action: done).keyboardShortcut(.defaultAction)
            }
        }
    }
}

private struct EntrySheet: View {
    let title: LocalizedStringKey
    let prompt: LocalizedStringKey
    var multiline = false
    let done: (String?) -> Void

    @State
    private var text = ""

    var body: some View {
        SheetFrame(title: title) {
            Text(prompt).foregroundStyle(.secondary)
            if multiline {
                TextEditor(text: $text)
                    .font(.system(.body, design: .monospaced))
                    .frame(height: 90)
                    .border(.separator)
            } else {
                TextField(text: $text, prompt: Text(verbatim: "XXXX-XXXX-XXXX")) { Text(prompt) }
                    .labelsHidden()
                    .font(.system(.title3, design: .monospaced))
            }
            HStack {
                Spacer()
                Button("Cancel") { done(nil) }.keyboardShortcut(.cancelAction)
                Button("Join") { done(text) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
    }
}

private struct ConfirmSheet: View {
    let title: LocalizedStringKey
    let message: LocalizedStringKey
    let action: LocalizedStringKey
    let done: (Bool) -> Void

    var body: some View {
        SheetFrame(title: title) {
            Text(message).fixedSize(horizontal: false, vertical: true)
            HStack {
                Spacer()
                Button("Cancel") { done(false) }.keyboardShortcut(.cancelAction)
                Button(action, role: .destructive) { done(true) }.keyboardShortcut(.defaultAction)
            }
        }
    }
}

/// On the new device: enter the code, compare the number, wait for approval.
private struct JoinWithCodeSheet: View {
    let server: String
    let done: () -> Void

    @State
    private var code = ""
    @State
    private var pending: KauriSync.PendingJoin?
    @State
    private var error: String?
    @State
    private var task: Task<Void, Never>?

    var body: some View {
        SheetFrame(title: "Join with a Pairing Code…") {
            if let pending {
                Text("joinNumberMessage \(String(format: "%04d", pending.number))")
                    .fixedSize(horizontal: false, vertical: true)
                Text(verbatim: String(format: "%04d", pending.number))
                    .font(.system(size: 40, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .frame(maxWidth: .infinity)
                HStack {
                    ProgressView().controlSize(.small)
                    Text("Waiting for the other device…").foregroundStyle(.secondary)
                }
            } else {
                Text("Enter the code shown on your other device").foregroundStyle(.secondary)
                TextField(text: $code, prompt: Text(verbatim: "XXXX-XXXX-XXXX")) { Text("Pairing Code") }
                    .labelsHidden()
                    .font(.system(.title3, design: .monospaced))
            }
            if let error {
                Text(verbatim: error).foregroundStyle(.red)
            }
            HStack {
                Spacer()
                Button("Cancel") {
                    task?.cancel()
                    done()
                }
                .keyboardShortcut(.cancelAction)
                if pending == nil {
                    Button("Join", action: join)
                        .keyboardShortcut(.defaultAction)
                        .disabled(KauriPairing.normalize(code) == nil || task != nil)
                }
            }
        }
    }

    private func join() {
        error = nil
        task = Task {
            defer { task = nil }
            do {
                KauriSync.shared.setServer(server)
                let join = try await KauriSync.shared.join(server: server, code: code)
                pending = join
                try await KauriSync.shared.waitForApproval(join)
                done()
            } catch is CancellationError {
            } catch {
                pending = nil
                self.error = error.localizedDescription
            }
        }
    }
}

/// On this device: show a code, then allow or deny the device that used it.
private struct AddDeviceSheet: View {
    let done: () -> Void

    @State
    private var request: KauriSync.PairingRequest?
    @State
    private var joined: KauriSync.PairingJoin?
    @State
    private var error: String?
    @State
    private var task: Task<Void, Never>?

    var body: some View {
        SheetFrame(title: "Add a Device…") {
            if let joined {
                Text("allowDeviceTitle \(joined.name)").font(.headline)
                Text("allowDeviceMessage \(String(format: "%04d", joined.number))")
                Text(verbatim: String(format: "%04d", joined.number))
                    .font(.system(size: 40, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .frame(maxWidth: .infinity)
            } else if let request {
                Text("pairingCodeMessage").fixedSize(horizontal: false, vertical: true)
                Text(verbatim: request.code)
                    .font(.system(size: 32, weight: .semibold, design: .monospaced))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity)
                HStack {
                    ProgressView().controlSize(.small)
                    Text("Waiting for the other device…").foregroundStyle(.secondary)
                }
            } else {
                ProgressView().frame(maxWidth: .infinity)
            }
            if let error {
                Text(verbatim: error).foregroundStyle(.red)
            }
            HStack {
                Spacer()
                if let joined {
                    Button("Deny") {
                        Task { await KauriSync.shared.deny(joined) }
                        done()
                    }
                    Button("Allow") {
                        Task {
                            do {
                                try await KauriSync.shared.allow(joined)
                                done()
                            } catch {
                                self.error = error.localizedDescription
                            }
                        }
                    }
                    .keyboardShortcut(.defaultAction)
                } else {
                    Button("Cancel") {
                        task?.cancel()
                        if let request { Task { await KauriSync.shared.cancelPairing(request) } }
                        done()
                    }
                    .keyboardShortcut(.cancelAction)
                }
            }
        }
        .task {
            do {
                let opened = try await KauriSync.shared.openPairing()
                request = opened
                joined = try await KauriSync.shared.waitForJoin(opened)
            } catch is CancellationError {
            } catch {
                self.error = error.localizedDescription
            }
        }
    }
}
