// Deep Link Engine — Android SDK
// Standalone Gradle build. It is intentionally NOT part of the .NET solution.

pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

@Suppress("UnstableApiUsage")
dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "dle-android"

include(":dle-sdk")
include(":sample")
