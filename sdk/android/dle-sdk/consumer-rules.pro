# Deep Link Engine SDK — rules merged into every consuming application's R8/ProGuard config.

# kotlinx.serialization: keep the generated serializers of the SDK's wire models.
-keepattributes *Annotation*, InnerClasses
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

# androidx.security:security-crypto is compileOnly in the SDK. When the host application does not
# ship it, R8 must not fail on the unresolved references; the SDK checks for the class at runtime.
-dontwarn androidx.security.crypto.**

# OkHttp's own optional dependencies.
-dontwarn okhttp3.internal.platform.**
-dontwarn org.conscrypt.**
-dontwarn org.bouncycastle.**
-dontwarn org.openjsse.**
