package sk.magors.dle

/**
 * Deterministic evidence handed to [DleClient.resolve] beyond what the SDK collects itself.
 *
 * A plain `resolve()` happens once per installation and its result is persisted (TC-143). A call
 * that carries new evidence is a new question and goes to the server again, unless a
 * deterministic match is already stored.
 *
 * @property claimCode the six characters the user read off the interstitial page and typed in
 * (S3, FR-184). Normalised like the server does: whitespace and hyphens removed, upper cased.
 * Never obtained from the clipboard and never transported over a custom URI scheme (FR-227,
 * spec §A.2.3).
 * @property loginKey an opaque, already hashed account identifier that the website recorded on
 * the click when the same user was signed in there (S2, FR-185). Never a raw e-mail address or
 * user id: hash it with a key only your backend knows, on both ends.
 */
public class ResolveOptions @JvmOverloads constructor(
    public val claimCode: String? = null,
    public val loginKey: String? = null,
) {
    /** Whether this call carries evidence beyond what the SDK gathers on its own. */
    public val hasExplicitEvidence: Boolean
        get() = !claimCode.isNullOrBlank() || !loginKey.isNullOrBlank()

    override fun toString(): String =
        "ResolveOptions(claimCode=${if (claimCode == null) "null" else "<redacted>"}, " +
            "loginKey=${if (loginKey == null) "null" else "<redacted>"})"

    public companion object {
        /** No extra evidence. */
        @JvmField
        public val NONE: ResolveOptions = ResolveOptions()

        /** Evidence for the claim code path. */
        @JvmStatic
        public fun claimCode(code: String): ResolveOptions = ResolveOptions(claimCode = code)

        /** Evidence for the login reconciliation path. */
        @JvmStatic
        public fun loginKey(key: String): ResolveOptions = ResolveOptions(loginKey = key)
    }
}
