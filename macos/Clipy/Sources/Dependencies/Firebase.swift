//
//  Firebase.swift
//
//  Clipy
//  GitHub: https://github.com/clipy
//  HP: https://clipy-app.com
//
//  Created by Shunsuke Furubayashi on 2026/06/25.
//
//  Copyright © 2015-2026 Clipy Project.
//
//  Kauri Clip: analytics and crash reporting are removed. The dependency stays
//  as a no-op so callers need no changes.
//

import Dependencies
import DependenciesMacros

@DependencyClient
struct Firebase {
    var configure: () -> Void
    var logEvent: (_ event: Event) -> Void

    enum Event {
        case popUpMenu(MenuType)
        case popUpSnippetFolderMenu
    }
}

extension DependencyValues {
    var firebase: Firebase {
        get { self[FirebaseKey.self] }
        set { self[FirebaseKey.self] = newValue }
    }

    private enum FirebaseKey: DependencyKey {
        static let liveValue = Firebase(configure: {}, logEvent: { _ in })
        static let testValue = Firebase(configure: {}, logEvent: { _ in })
    }
}
