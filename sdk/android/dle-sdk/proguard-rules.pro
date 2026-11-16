# Deep Link Engine SDK — rules applied when the SDK module itself is built with minification.
# Consumers get consumer-rules.pro merged automatically; this file only matters for a minified
# build of the library or its sample.

# kotlinx.serialization: keep the generated serializers of the wire models in net.kyberware.dle.internal.
-keepattributes *Annotation*, InnerClasses, Signature
-dontnote kotlinx.serialization.**
-keepclassmembers class net.kyberware.dle.** {
    *** Companion;
}
-if @kotlinx.serialization.Serializable class net.kyberware.dle.**
-keepclassmembers class <1> {
    static <1>$Companion Companion;
}
-if @kotlinx.serialization.Serializable class net.kyberware.dle.** {
    static **$* *;
}
-keepclassmembers class <2>$<3> {
    kotlinx.serialization.KSerializer serializer(...);
}
-if @kotlinx.serialization.Serializable class net.kyberware.dle.** {
    public static ** INSTANCE;
}
-keepclassmembers class <1> {
    public static <1> INSTANCE;
    kotlinx.serialization.KSerializer serializer(...);
}
-keep,includedescriptorclasses class net.kyberware.dle.**$$serializer { *; }

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
-keep public class net.kyberware.dle.Dle { public *; }
-keep public class net.kyberware.dle.DleClient { public *; }
