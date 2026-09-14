// Deep Link Engine — Android SDK module.
//
// Thin by design (spec §C.5): the only runtime dependencies are the platform HTTP client
// (OkHttp), coroutines, kotlinx.serialization and Google's Install Referrer library. Every
// dependency of an SDK becomes a dependency of every customer application, so nothing else is
// added. androidx.security:security-crypto is compileOnly: the SDK uses it when the host
// application ships it and falls back to plain SharedPreferences otherwise.

import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    alias(libs.plugins.android.library)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.serialization)
    `maven-publish`
}

val sdkGroup: String = providers.gradleProperty("dle.sdk.group").getOrElse("sk.magors.dle")
val sdkVersion: String = providers.gradleProperty("dle.sdk.version").getOrElse("0.0.0-SNAPSHOT")

group = sdkGroup
version = sdkVersion

android {
    namespace = "sk.magors.dle"
    compileSdk = 35

    defaultConfig {
        minSdk = 23
        consumerProguardFiles("consumer-rules.pro")
        // Surfaced as Dle.VERSION so a support engineer can tell which build produced a request.
        buildConfigField("String", "SDK_VERSION", "\"$sdkVersion\"")
    }

    buildFeatures {
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    testOptions {
        unitTests {
            // android.util.Log and friends return defaults instead of throwing on the JVM. Every
            // internal component is written so that no test needs a real Android class.
            isReturnDefaultValues = true
            all { test ->
                test.useJUnitPlatform()
                test.testLogging {
                    events("passed", "skipped", "failed")
                    exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
                }
            }
        }
    }

    publishing {
        singleVariant("release") {
            withSourcesJar()
        }
    }

    lint {
        abortOnError = true
    }
}

kotlin {
    // Every public declaration must carry an explicit visibility modifier and an explicit return
    // type: the API surface is a contract, not an accident.
    explicitApi()

    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
        freeCompilerArgs.add("-Xjvm-default=all")
    }
}

dependencies {
    implementation(libs.kotlinx.coroutines.core)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.okhttp)
    implementation(libs.installreferrer)

    // Optional at runtime, see InstallIdStore / KeyValueStore.
    compileOnly(libs.androidx.security.crypto)

    testImplementation(libs.junit.jupiter)
    testImplementation(libs.kotlinx.coroutines.test)
    testImplementation(libs.okhttp.mockwebserver)
    testRuntimeOnly(libs.junit.platform.launcher)
}

publishing {
    publications {
        register<MavenPublication>("release") {
            groupId = sdkGroup
            artifactId = "dle-sdk"
            version = sdkVersion

            afterEvaluate {
                from(components["release"])
            }

            pom {
                name.set("Deep Link Engine Android SDK")
                description.set(
                    "Android client for the Deep Link Engine: Install Referrer, App Links reporting " +
                        "and batched event delivery against the DLE SDK plane.",
                )
                url.set("https://github.com/MAGORS-organisation/Deep-Links-Engine")
                licenses {
                    license {
                        name.set("MIT")
                        url.set("https://opensource.org/licenses/MIT")
                    }
                }
                scm {
                    url.set("https://github.com/MAGORS-organisation/Deep-Links-Engine")
                    connection.set("scm:git:https://github.com/MAGORS-organisation/Deep-Links-Engine.git")
                }
            }
        }
    }
}
