package sk.magors.dle

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import sk.magors.dle.internal.EventQueue
import sk.magors.dle.internal.EventWire
import sk.magors.dle.internal.MAX_EVENTS_PER_BATCH
import java.io.File
import kotlin.random.Random

/** The offline buffer behind `POST /v1/events` (FR-226). */
@OptIn(ExperimentalCoroutinesApi::class)
class EventQueueTest {
    @TempDir
    lateinit var dir: File

    private val clock = ManualClock(CONTRACT_MOMENT_MILLIS)

    private class RecordingSender(private val failures: ArrayDeque<DleException> = ArrayDeque()) {
        val batches = ArrayList<List<EventWire>>()
        var attempts = 0

        suspend fun send(batch: List<EventWire>) {
            attempts++
            failures.removeFirstOrNull()?.let { throw it }
            batches += batch
        }
    }

    // The queue runs on the TestScope itself, not on backgroundScope: advanceUntilIdle() only drives
    // foreground work, so a queue launched in the background would never drain under it. Every
    // drain terminates on its own (success, non-retriable refusal, or the retry budget), so a
    // foreground queue cannot keep a test alive.
    private fun kotlinx.coroutines.CoroutineScope.queue(
        sender: RecordingSender,
        maxSize: Int = 1000,
        flushDelayMillis: Long = 3_000L,
    ): EventQueue = EventQueue(
        directory = dir,
        maxSize = maxSize,
        flushDelayMillis = flushDelayMillis,
        sender = sender::send,
        scope = this,
        log = silentLog(),
        clock = clock,
        random = Random(1),
    )

    @Test
    fun dropsTheOldestEventWhenFull() = runTest {
        val sender = RecordingSender()
        val queue = queue(sender, maxSize = 3)

        for (i in 1..5) queue.enqueue(DleEvent.custom("e$i"))

        assertEquals(3, queue.size)
        assertEquals(listOf("e3", "e4", "e5"), queue.snapshot().map { it.name })
    }

    @Test
    fun deliversInBatchesOfAtMostOneHundred() = runTest {
        val sender = RecordingSender()
        val queue = queue(sender)
        for (i in 1..250) queue.enqueue(DleEvent.custom("e$i"))

        queue.flush()
        advanceUntilIdle()

        assertEquals(listOf(MAX_EVENTS_PER_BATCH, MAX_EVENTS_PER_BATCH, 50), sender.batches.map { it.size })
        assertEquals("e1", sender.batches[0][0].name)
        assertEquals("e250", sender.batches[2][49].name)
        assertEquals(0, queue.size)
        assertFalse(File(dir, EventQueue.FILE_NAME).exists(), "an empty buffer leaves no file behind")
    }

    @Test
    fun flushesAutomaticallyAfterTheConfiguredDelay() = runTest {
        val sender = RecordingSender()
        val queue = queue(sender, flushDelayMillis = 3_000L)

        queue.enqueue(DleEvent.firstOpen())
        advanceTimeBy(2_999L)
        runCurrent()
        assertEquals(0, sender.batches.size, "nothing goes out before the delay")

        advanceTimeBy(2L)
        runCurrent()
        assertEquals(1, sender.batches.size)
        assertEquals("first_open", sender.batches[0][0].type)
    }

    @Test
    fun retriesWithBackoffAndKeepsTheEventsUntilDelivered() = runTest {
        val sender = RecordingSender(ArrayDeque<DleException>(listOf(DleException.Network("offline"), DleException.Timeout("slow"))))
        val queue = queue(sender)
        queue.enqueue(DleEvent.session())

        queue.flush()
        runCurrent()
        assertEquals(1, sender.attempts)
        assertEquals(1, queue.consecutiveFailures)
        assertEquals(1, queue.size, "a retriable failure keeps the batch")

        // First retry waits at least half of the 1 s base backoff.
        advanceTimeBy(400L)
        runCurrent()
        assertEquals(1, sender.attempts)

        advanceUntilIdle()
        assertEquals(3, sender.attempts)
        assertEquals(1, sender.batches.size)
        assertEquals(0, queue.size)
        assertEquals(0, queue.consecutiveFailures)
    }

    @Test
    fun backoffGrowsExponentiallyWithJitterAndHonoursRetryAfter() = runTest {
        val queue = queue(RecordingSender())

        for (attempt in 1..20) {
            val ceiling = minOf(EventQueue.BASE_BACKOFF_MILLIS shl minOf(attempt - 1, EventQueue.MAX_BACKOFF_EXPONENT), EventQueue.MAX_BACKOFF_MILLIS)
            repeat(20) {
                val wait = queue.backoffMillis(attempt, null)
                assertTrue(wait in (ceiling / 2)..ceiling, "attempt $attempt produced $wait outside [${ceiling / 2}, $ceiling]")
            }
        }
        assertTrue(queue.backoffMillis(1, 30_000L) >= 30_000L, "Retry-After is a floor")
        assertTrue(queue.backoffMillis(20, null) <= EventQueue.MAX_BACKOFF_MILLIS)
    }

    @Test
    fun dropsABatchTheServerRefusesForGood() = runTest {
        val sender = RecordingSender(ArrayDeque<DleException>(listOf(DleException.Http(status = 400))))
        val queue = queue(sender)
        queue.enqueue(DleEvent.custom("bad"))

        queue.flush()
        advanceUntilIdle()

        assertEquals(1, sender.attempts)
        assertEquals(0, queue.size)
        assertEquals(0, sender.batches.size)
    }

    @Test
    fun pausesAfterTheRetryBudgetAndResumesOnTheNextFlush() = runTest {
        val failures = ArrayDeque<DleException>(List(EventQueue.MAX_ATTEMPTS_PER_RUN + 1) { DleException.Network("offline") })
        val sender = RecordingSender(failures)
        val queue = queue(sender)
        queue.enqueue(DleEvent.session())

        queue.flush()
        advanceUntilIdle()
        assertEquals(EventQueue.MAX_ATTEMPTS_PER_RUN + 1, sender.attempts)
        assertEquals(1, queue.size, "the event is kept for later")

        queue.flush()
        advanceUntilIdle()
        assertEquals(1, sender.batches.size)
        assertEquals(0, queue.size)
    }

    @Test
    fun survivesProcessDeath() = runTest {
        val first = queue(RecordingSender(), flushDelayMillis = 60_000L)
        first.enqueue(DleEvent.custom("one"))
        first.enqueue(DleEvent.conversion("purchase", 24.9, "eur"))

        val second = queue(RecordingSender(), flushDelayMillis = 60_000L)

        assertEquals(2, second.size)
        assertEquals(listOf("one", "purchase"), second.snapshot().map { it.name })
        assertEquals("EUR", second.snapshot()[1].currency)
        assertEquals("2026-09-03T10:00:00+00:00", second.snapshot()[0].ts, "the enqueue time is stamped, not the send time")
    }

    @Test
    fun aCorruptBufferFileStartsEmptyInsteadOfCrashing() = runTest {
        File(dir, EventQueue.FILE_NAME).writeText("{not json")

        val queue = queue(RecordingSender())

        assertEquals(0, queue.size)
    }

    @Test
    fun neverThrowsToTheCaller() = runTest {
        val broken = EventQueue(
            directory = dir,
            maxSize = 10,
            flushDelayMillis = 0L,
            sender = { throw IllegalStateException("bug in the sender") },
            scope = this,
            log = silentLog(),
            clock = clock,
        )

        broken.enqueue(DleEvent.session())
        advanceUntilIdle()

        assertEquals(0, broken.size, "an unexpected failure drops the batch rather than poisoning the buffer")
    }
}
