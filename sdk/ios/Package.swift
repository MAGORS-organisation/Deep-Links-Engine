// swift-tools-version: 6.0
//
// Deep Link Engine (DLE) — iOS SDK.
// Self-hosted, EU-first deep linking and attribution. MIT licensed.
//
// Deliberately dependency-free: every transitive dependency of an SDK becomes a
// dependency of every customer application (spec §C.5). Networking is URLSession only.

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
            path: "Sources/DleSDK",
            resources: [
                // Apple privacy manifest (spec §E.7 rule 4). SwiftPM ships it inside
                // the target's resource bundle, where Xcode aggregates it at build time.
                .copy("PrivacyInfo.xcprivacy")
            ],
            swiftSettings: [
                .swiftLanguageMode(.v6)
            ]
        ),
        .testTarget(
            name: "DleSDKTests",
            dependencies: ["DleSDK"],
            path: "Tests/DleSDKTests",
            swiftSettings: [
                .swiftLanguageMode(.v6)
            ]
        )
    ]
)
