// Root build file. No configuration is applied here on purpose — every module owns its own setup.
buildscript {
    dependencies {
        // AGP 9 compiles Kotlin itself (built-in Kotlin); this pins the Kotlin version it uses to the
        // catalog's, the same version as the serialization compiler plugin below.
        classpath(libs.kotlin.gradle.plugin)
    }
}

plugins {
    alias(libs.plugins.android.library) apply false
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.serialization) apply false
}
