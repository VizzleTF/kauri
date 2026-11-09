import Foundation
import XCTest
@testable import Clipy

final class BundleExtensionsTests: XCTestCase {
    func testAppNameFromMainBundle() {
        #if DEBUG
        XCTAssertEqual(Bundle.main.appName, "KauriDEBUG")
        #else
        XCTAssertEqual(Bundle.main.appName, "Kauri")
        #endif
    }
}
