package sk.magors.dle.internal

import java.security.SecureRandom

/**
 * W3C Trace Context (NFR-12): one `traceparent` per request so the SDK span and the server span it
 * caused belong to one trace. The identifiers are random, never derived from anything about the
 * device or the installation.
 */
internal object TraceContext {
    /** Shape of a value produced by [newTraceparent]. */
    val PATTERN: Regex = Regex("^00-[0-9a-f]{32}-[0-9a-f]{16}-01$")

    private val random = SecureRandom()

    /** A fresh `traceparent` header value: version 00, sampled. */
    fun newTraceparent(): String {
        val trace = ByteArray(TRACE_ID_BYTES)
        val span = ByteArray(SPAN_ID_BYTES)
        random.nextBytes(trace)
        random.nextBytes(span)
        // An all zero id is invalid per the specification and would be dropped by collectors.
        if (trace.all { it == 0.toByte() }) trace[trace.lastIndex] = 1
        if (span.all { it == 0.toByte() }) span[span.lastIndex] = 1
        return "00-${hex(trace)}-${hex(span)}-01"
    }

    fun hex(bytes: ByteArray): String {
        val out = StringBuilder(bytes.size * 2)
        for (b in bytes) {
            val v = b.toInt() and 0xff
            out.append(HEX[v ushr 4]).append(HEX[v and 0x0f])
        }
        return out.toString()
    }

    private const val TRACE_ID_BYTES = 16
    private const val SPAN_ID_BYTES = 8
    private const val HEX = "0123456789abcdef"
}
