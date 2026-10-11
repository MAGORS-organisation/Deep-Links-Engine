// swift-tools-version: 6.0
//
// Deep Link Engine (DLE) — iOS SDK (Root Package Manifest).
// Self-hosted, EU-first deep linking and attribution. MIT licensed.
//
// Enables Xcode and Swift Package Manager to consume DleSDK directly via repository URL.

import PackageDescription

let package = Package(
    name: "DleSDK",
    platforms: [
        .iOS(.v15),
        .macCatalyst(.v15),
        .tvOS(.v15),
        .visionOS(.v1),
        .macOS(.v12)
    ],
    products: [
        .library(name: "DleSDK", targets: ["DleSDK"])
    ],
    dependencies: [],
    targets: [
        .target(
            name: "DleSDK",
            dependencies: [],
            path: "sdk/ios/Sources/DleSDK",
            resources: [
                .copy("PrivacyInfo.xcprivacy")
            ],
            swiftSettings: [
                .swiftLanguageMode(.v6)
            ]
        ),
        .testTarget(
            name: "DleSDKTests",
            dependencies: ["DleSDK"],
            path: "sdk/ios/Tests/DleSDKTests",
            swiftSettings: [
                .swiftLanguageMode(.v6)
            ]
        )
    ]
)
