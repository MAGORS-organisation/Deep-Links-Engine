package sk.magors.dle.internal

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.SerializationException
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json
import sk.magors.dle.DleEvent
import sk.magors.dle.DleException
import java.io.File
import java.io.IOException
import kotlin.random.Random

/**
 * The offline event buffer behind `POST /v1/events` (FR-226).
 *
 * - Bounded to [maxSize] entries; when full, the oldest event is dropped so the buffer can never
 *   grow without limit on a device that is offline for weeks.
 * - Persisted to a private file as JSON after every change, so events survive process death.
 * - Delivered in batches of at most [MAX_EVENTS_PER_BATCH], the server's limit.
 * - Retried with exponential backoff and jitter on retriable failures; `Retry-After` from the
 *   server is honoured when it is longer. A batch the server refuses for good (a 4xx other than
 *   408 and 429) is dropped: it would be refused tomorrow too.
 * - Flushed automatically [flushDelayMillis] after an enqueue and on demand through [flush].
 * - Never throws to the caller. An analytics buffer that crashes the host is worse than one that
 *   loses an event.
 *
 * @param directory where the buffer file lives; created when missing.
 * @param maxSize [sk.magors.dle.DleConfig.maxQueueSize].
 * @param flushDelayMillis [sk.magors.dle.DleConfig.flushDelayMillis].
 * @param sender delivers one batch; throws [DleException] on failure.
 * @param scope the coroutine scope delivery runs in.
 * @param log the SDK log. Event counts and types only; never a URL or a property value.
 * @param clock source of `ts` for events without a timestamp.
 * @param random source of the backoff jitter; injectable for deterministic tests.
 * @param json the serializer.
 */
internal class EventQueue(
    directory: File,
    private val maxSize: Int,
    private val flushDelayMillis: Long,
    private val sender: suspend (List<EventWire>) -> Unit,
    private val scope: CoroutineScope,
    private val log: SdkLog,
    private val clock: Clock = Clock.SYSTEM,
    private val random: Random = Random.Default,
    private val json: Json = DleJson,
) {
    private val file = File(directory, FILE_NAME)
    private val lock = Any()
    private val events = ArrayList<EventWire>()
    private val drainLock = Mutex()

    @Volatile
    private var scheduled: Job? = null

    /** Number of retriable failures in a row; reset on success. Exposed for tests. */
    @Volatile
    var consecutiveFailures: Int = 0
        private set

    init {
        load()
    }

    /** Number of buffered events. */
    val size: Int
        get() = synchronized(lock) { events.size }

    /** A copy of the buffered events, oldest first. */
    fun snapshot(): List<EventWire> = synchronized(lock) { ArrayList(events) }

    /**
     * Buffers one event and schedules a flush. Drops the oldest event when the buffer is full.
     */
    fun enqueue(event: DleEvent) {
        try {
            val wire = event.toWire(clock.nowMillis())
            synchronized(lock) {
                while (events.size >= maxSize) {
                    events.removeAt(0)
                    log.warn("event buffer full ($maxSize), oldest event dropped")
                }
                events.add(wire)
                persistLocked()
            }
            scheduleFlush()
        } catch (e: RuntimeException) {
            log.error("event could not be buffered", e)
        }
    }

    /** Starts a delivery attempt now, unless one is already running. */
    fun flush() {
        try {
            scheduled?.cancel()
            scheduled = null
            scope.launch { drain() }
        } catch (e: RuntimeException) {
            log.error("flush could not be started", e)
        }
    }

    /** Drops every buffered event. Used when the installation is erased. */
    fun clear() {
        synchronized(lock) {
            events.clear()
            persistLocked()
        }
    }

    private fun scheduleFlush() {
        if (flushDelayMillis <= 0L) {
            flush()
            return
        }
        val current = scheduled
        if (current != null && current.isActive) return
        scheduled = scope.launch {
            delay(flushDelayMillis)
            drain()
        }
    }

    /**
     * Delivers batches until the buffer is empty, a non transient failure is met or the retry
     * budget of this run is spent. Only one drain runs at a time; a second caller waits and then
     * finds nothing left to do.
     */
    suspend fun drain() {
        if (!drainLock.tryLock()) return
        try {
            while (true) {
                val batch = synchronized(lock) { ArrayList(events.take(MAX_EVENTS_PER_BATCH)) }
                if (batch.isEmpty()) return
                try {
                    sender(batch)
                    removeDelivered(batch)
                    consecutiveFailures = 0
                    log.debug("delivered ${batch.size} event(s)")
                } catch (e: CancellationException) {
                    throw e
                } catch (e: DleException) {
                    if (!e.isRetriable) {
                        log.warn("server refused ${batch.size} event(s), dropped: ${e.message}")
                        removeDelivered(batch)
                        continue
                    }
                    consecutiveFailures++
                    if (consecutiveFailures > MAX_ATTEMPTS_PER_RUN) {
                        log.warn("event delivery paused after $MAX_ATTEMPTS_PER_RUN failures; kept ${size} event(s)")
                        return
                    }
                    val retryAfter = (e as? DleException.Http)?.retryAfterMillis
                    val wait = backoffMillis(consecutiveFailures, retryAfter)
                    log.debug("event delivery failed (${e.message}), retrying in $wait ms")
                    delay(wait)
                } catch (e: RuntimeException) {
                    // A bug in the sender must not poison the buffer forever.
                    log.error("event delivery failed unexpectedly, ${batch.size} event(s) dropped", e)
                    removeDelivered(batch)
                }
            }
        } finally {
            drainLock.unlock()
        }
    }

    /**
     * Exponential backoff with full jitter, floored by the server's `Retry-After` when given.
     *
     * @param attempt 1 for the first retry.
     * @param retryAfterMillis the server's hint, when any.
     */
    fun backoffMillis(attempt: Int, retryAfterMillis: Long?): Long {
        val exponent = (attempt - 1).coerceIn(0, MAX_BACKOFF_EXPONENT)
        val ceiling = (BASE_BACKOFF_MILLIS shl exponent).coerceAtMost(MAX_BACKOFF_MILLIS)
        val jittered = ceiling / 2 + random.nextLong(ceiling / 2 + 1)
        return maxOf(jittered, retryAfterMillis ?: 0L)
    }

    private fun removeDelivered(batch: List<EventWire>) {
        synchronized(lock) {
            // The batch is a prefix of the buffer by identity: nothing is removed from the front
            // except here, and enqueue appends only.
            var removed = 0
            while (removed < batch.size && events.isNotEmpty() && events[0] === batch[removed]) {
                events.removeAt(0)
                removed++
            }
            persistLocked()
        }
    }

    private fun load() {
        synchronized(lock) {
            if (!file.exists()) return
            try {
                val text = file.readText(Charsets.UTF_8)
                if (text.isBlank()) return
                events.addAll(json.decodeFromString(ListSerializer(EventWire.serializer()), text))
                while (events.size > maxSize) events.removeAt(0)
            } catch (e: IOException) {
                log.warn("event buffer unreadable, starting empty", e)
            } catch (e: SerializationException) {
                log.warn("event buffer corrupt, starting empty", e)
                file.delete()
            } catch (e: IllegalArgumentException) {
                log.warn("event buffer corrupt, starting empty", e)
                file.delete()
            }
        }
    }

    /** Writes the buffer atomically: a full file or the previous one, never a torn one. */
    private fun persistLocked() {
        try {
            val parent = file.parentFile
            if (parent != null && !parent.exists()) parent.mkdirs()
            if (events.isEmpty()) {
                if (file.exists()) file.delete()
                return
            }
            val tmp = File(file.path + ".tmp")
            tmp.writeText(json.encodeToString(ListSerializer(EventWire.serializer()), events), Charsets.UTF_8)
            if (!tmp.renameTo(file)) {
                file.delete()
                if (!tmp.renameTo(file)) throw IOException("rename failed")
            }
        } catch (e: IOException) {
            log.warn("event buffer could not be persisted", e)
        } catch (e: SecurityException) {
            log.warn("event buffer could not be persisted", e)
        }
    }

    internal companion object {
        const val FILE_NAME: String = "sk.magors.dle.events.json"
        const val BASE_BACKOFF_MILLIS: Long = 1_000L
        const val MAX_BACKOFF_MILLIS: Long = 60_000L
        const val MAX_BACKOFF_EXPONENT: Int = 6
        const val MAX_ATTEMPTS_PER_RUN: Int = 5
    }
}
