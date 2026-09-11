// Deep Link Engine — sample application.
//
// The smallest integration that exercises every path: initialise, resolve, App Links, consent.
// It talks to a dle-control running on the development machine (10.0.2.2 from the emulator).

import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
}

android {
    namespace = "sk.magors.dle.sample"
    compileSdk = 35

    defaultConfig {
        applicationId = "sk.magors.dle.sample"
        minSdk = 23
        targetSdk = 35
        versionCode = 1
        versionName = "0.1.0"

        // Override on the command line: -Pdle.sample.endpoint=https://links.example.sk -Pdle.sample.sdkKey=dle_pk_…
        val endpoint = providers.gradleProperty("dle.sample.endpoint").getOrElse("http://10.0.2.2:8081")
        val sdkKey = providers.gradleProperty("dle.sample.sdkKey").getOrElse("dle_pk_sample")
        buildConfigField("String", "DLE_ENDPOINT", "\"$endpoint\"")
        buildConfigField("String", "DLE_SDK_KEY", "\"$sdkKey\"")
    }

    buildFeatures {
        buildConfig = true
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

dependencies {
    implementation(project(":dle-sdk"))

    // Optional for the SDK; the sample ships it so the install id lands in EncryptedSharedPreferences.
    implementation(libs.androidx.security.crypto)

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.kotlinx.coroutines.android)
}
