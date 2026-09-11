# Deep Link Engine SDK — rules applied when the SDK module itself is built with minification.
# Consumers get consumer-rules.pro merged automatically; this file only matters for a minified
# build of the library or its sample.

# kotlinx.serialization: keep the generated serializers of the wire models in sk.magors.dle.internal.
-keepattributes *Annotation*, InnerClasses, Signature
-dontnote kotlinx.serialization.**
-keepclassmembers class sk.magors.dle.** {
    *** Companion;
}
-if @kotlinx.serialization.Serializable class sk.magors.dle.**
-keepclassmembers class <1> {
    static <1>$Companion Companion;
}
-if @kotlinx.serialization.Serializable class sk.magors.dle.** {
    static **$* *;
}
-keepclassmembers class <2>$<3> {
    kotlinx.serialization.KSerializer serializer(...);
}
-if @kotlinx.serialization.Serializable class sk.magors.dle.** {
    public static ** INSTANCE;
}
-keepclassmembers class <1> {
    public static <1> INSTANCE;
    kotlinx.serialization.KSerializer serializer(...);
}
-keep,includedescriptorclasses class sk.magors.dle.**$$serializer { *; }

# Google Play Install Referrer: the client talks to the Play Store over AIDL; keep its surface.
-keep class com.android.installreferrer.** { *; }
-dontwarn com.android.installreferrer.**

# androidx.security:security-crypto is compileOnly; the SDK checks for it at runtime.
-dontwarn androidx.security.crypto.**

# OkHttp's optional platform integrations.
-dontwarn okhttp3.internal.platform.**
-dontwarn org.conscrypt.**
-dontwarn org.bouncycastle.**
-dontwarn org.openjsse.**

# The public API is a contract: keep it readable in stack traces from the field.
-keep public class sk.magors.dle.Dle { public *; }
-keep public class sk.magors.dle.DleClient { public *; }
