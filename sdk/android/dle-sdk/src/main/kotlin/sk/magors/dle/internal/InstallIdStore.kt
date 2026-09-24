package sk.magors.dle.internal

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import java.io.IOException
import java.security.GeneralSecurityException
import java.util.UUID

/**
 * The installation identifier: a random UUID minted once and kept for the life of the
 * installation. It is the `install_id` of every request (FR-188, TC-143) and the key under which
 * the engine's deletion endpoint erases what it holds about this device (spec §E.6.3).
 *
 * It is random, never derived from a hardware identifier, the advertising identifier or anything
 * else about the device (FR-227). Uninstalling the application discards it.
 *
 * @param prefs the backing store. See [open] for how it is chosen.
 */
internal class InstallIdStore(private val prefs: SharedPreferences) {
    /** Returns the identifier, minting and persisting one on the first call. */
    @Synchronized
    fun getOrCreate(): String {
        val existing = prefs.getString(KEY_INSTALL_ID, null)
        if (!existing.isNullOrBlank()) return existing
        val minted = UUID.randomUUID().toString()
        prefs.edit().putString(KEY_INSTALL_ID, minted).commit()
        return minted
    }

    /** Drops the identifier; the next [getOrCreate] mints a new one. */
    @Synchronized
    fun clear() {
        prefs.edit().remove(KEY_INSTALL_ID).commit()
    }

    internal companion object {
        const val KEY_INSTALL_ID: String = "install_id"
        const val ENCRYPTED_FILE: String = "sk.magors.dle.install"
        const val PLAIN_FILE: String = "sk.magors.dle.install.plain"

        /**
         * Opens the store. `androidx.security:security-crypto` is `compileOnly` in the SDK: when the
         * host application ships it and the Android Keystore is usable, the identifier lives in
         * [EncryptedSharedPreferences]; otherwise (library absent, keystore broken or corrupted,
         * which happens on some devices after a backup restore) plain [SharedPreferences] are used.
         * The fallback is chosen once per process; the two files are distinct, so a device that
         * flips between them mints a new identifier, which merely looks like a reinstall.
         */
        fun open(context: Context, log: SdkLog): InstallIdStore {
            val app = context.applicationContext
            val encrypted = try {
                openEncrypted(app)
            } catch (e: LinkageError) {
                // The library is not on the class path. Expected; nothing to report.
                log.debug("security-crypto not present, install id kept in plain SharedPreferences", e)
                null
            } catch (e: GeneralSecurityException) {
                log.warn("Android Keystore unavailable, install id kept in plain SharedPreferences", e)
                null
            } catch (e: IOException) {
                log.warn("encrypted preferences unreadable, install id kept in plain SharedPreferences", e)
                null
            } catch (e: RuntimeException) {
                // Keystore providers throw a variety of unchecked exceptions on broken devices.
                log.warn("encrypted preferences failed, install id kept in plain SharedPreferences", e)
                null
            }
            return InstallIdStore(encrypted ?: app.getSharedPreferences(PLAIN_FILE, Context.MODE_PRIVATE))
        }

        /** Kept in its own method so a missing class fails here, inside the `try`, not in [open]. */
        private fun openEncrypted(context: Context): SharedPreferences {
            val masterKey = MasterKey.Builder(context)
                .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
                .build()
            return EncryptedSharedPreferences.create(
                context,
                ENCRYPTED_FILE,
                masterKey,
                EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
            )
        }
    }
}
