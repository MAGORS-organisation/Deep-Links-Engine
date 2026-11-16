package sk.magors.dle

/**
 * Every failure the SDK surfaces, as a sealed hierarchy so callers can branch on the kind rather
 * than parse a message. [isRetriable] tells the offline queue whether to keep a batch.
 */
public sealed class DleException(message: String, cause: Throwable? = null) : RuntimeException(message, cause) {
    /** Whether the same request may succeed later without any change on the caller's side. */
    public abstract val isRetriable: Boolean

    /** The SDK was configured wrongly. Fix the integration; retrying will not help. */
    public class Configuration(message: String) : DleException(message) {
        override val isRetriable: Boolean get() = false
    }

    /** An argument the application passed is unusable, for example a malformed claim code. */
    public class InvalidArgument(message: String) : DleException(message) {
        override val isRetriable: Boolean get() = false
    }

    /** The request never received a response: no connectivity, DNS failure, connection reset. */
    public class Network(message: String, cause: Throwable? = null) : DleException(message, cause) {
        override val isRetriable: Boolean get() = true
    }

    /** The request exceeded the configured timeout. */
    public class Timeout(message: String, cause: Throwable? = null) : DleException(message, cause) {
        override val isRetriable: Boolean get() = true
    }

    /**
     * The server answered with a non success status.
     *
     * @property status the HTTP status code.
     * @property problem the RFC 9457 problem document, when the server sent one.
     * @property retryAfterMillis the server's `Retry-After`, in milliseconds, when present.
     */
    public class Http(
        public val status: Int,
        public val problem: ProblemDocument? = null,
        public val retryAfterMillis: Long? = null,
        message: String = problem?.title ?: "HTTP $status",
    ) : DleException(message) {
        /** 408, 429 and every 5xx are retriable; a 4xx body the server refused today it refuses tomorrow. */
        override val isRetriable: Boolean
            get() = status == 429 || status == 408 || status >= 500

        /** `true` when the problem code is `rate-limited`. */
        public val isRateLimited: Boolean
            get() = status == 429 || problem?.code == ProblemDocument.CODE_RATE_LIMITED

        /** `true` when the SDK key was refused. */
        public val isUnauthorized: Boolean
            get() = status == 401 || problem?.code == ProblemDocument.CODE_UNAUTHORIZED

        /**
         * `true` when a claim code could not be redeemed. Read [ProblemDocument.canReissue]
         * to decide whether showing the interstitial again makes sense.
         */
        public val isClaimCodeInvalid: Boolean
            get() = problem?.code == ProblemDocument.CODE_CLAIM_CODE_INVALID
    }

    /** The server answered 2xx with a body the SDK cannot use. Refused rather than guessed at. */
    public class Malformed(message: String, cause: Throwable? = null) : DleException(message, cause) {
        override val isRetriable: Boolean get() = false
    }

    /** An unexpected failure inside the SDK. Please report it with the stack trace. */
    public class Internal(message: String, cause: Throwable? = null) : DleException(message, cause) {
        override val isRetriable: Boolean get() = false
    }
}

/**
 * An RFC 9457 problem document as the engine sends it. Branch on [code] — the last path segment
 * of [type], such as `rate-limited` — and the `CODE_` constants, never on the whole [type] URI:
 * the host it is published under may change, the codes never do.
 *
 * @property type the problem type URI.
 * @property title short human readable summary.
 * @property status the HTTP status the document carries.
 * @property detail explanation aimed at the integrator reading a log.
 * @property instance the request path.
 * @property reason machine readable reason of a claim code failure: `expired`, `consumed`,
 * `malformed`, `unknown` or `disabled`.
 * @property canReissue for a claim code failure, whether asking the user for a fresh code helps.
 */
public data class ProblemDocument(
    public val type: String? = null,
    public val title: String? = null,
    public val status: Int? = null,
    public val detail: String? = null,
    public val instance: String? = null,
    public val reason: String? = null,
    public val canReissue: Boolean? = null,
) {
    /** The problem code: the last path segment of [type], such as `rate-limited`, or `null`. */
    public val code: String?
        get() = codeOf(type)

    public companion object {
        /** The request body failed validation. */
        public const val CODE_VALIDATION_FAILED: String = "validation-failed"

        /** A rate limit was exceeded; `Retry-After` says how long to wait. */
        public const val CODE_RATE_LIMITED: String = "rate-limited"

        /** The SDK key is missing, malformed, unknown or inactive. */
        public const val CODE_UNAUTHORIZED: String = "unauthorized"

        /** The credential lacks the scope the operation needs. */
        public const val CODE_FORBIDDEN: String = "forbidden"

        /** The claim code is unknown, consumed or past its time to live. */
        public const val CODE_CLAIM_CODE_INVALID: String = "claim-code-invalid"

        /** A dependency the request needs is unavailable; the caller may retry. */
        public const val CODE_DEPENDENCY_UNAVAILABLE: String = "dependency-unavailable"

        /**
         * The code of a problem type URI: its last path segment, ignoring a query, a fragment and
         * a trailing slash.
         *
         * @param type the problem type URI as received.
         * @return the code, or `null` when there is none.
         */
        @JvmStatic
        public fun codeOf(type: String?): String? {
            val path = type?.substringBefore('?')?.substringBefore('#')?.trimEnd('/') ?: return null
            return path.substringAfterLast('/').ifEmpty { null }
        }
    }
}
