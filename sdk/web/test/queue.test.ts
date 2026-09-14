import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { DleError } from '../src/api';
import { QUEUE_KEY, createQueue } from '../src/queue';
import { createMemoryStore } from '../src/storage';
import type { KeyValueStore } from '../src/storage';
import type { DleEvent } from '../src/types';
import { createFakeTarget } from './helpers';

interface Sent {
  readonly events: readonly DleEvent[];
  readonly keepalive: boolean;
}

function harness(
  store: KeyValueStore = createMemoryStore('t'),
  responder: (call: Sent, index: number) => Promise<void> = () => Promise.resolve(),
  extra: Partial<Parameters<typeof createQueue>[0]> = {},
) {
  const sent: Sent[] = [];
  const win = createFakeTarget();
  const doc = createFakeTarget();
  const queue = createQueue({
    store,
    mayFlush: () => true,
    flushIntervalMs: 1000,
    send: (events, keepalive) => {
      const call = { events, keepalive };
      sent.push(call);
      return responder(call, sent.length - 1);
    },
    window: win,
    document: doc,
    ...extra,
  });
  return { queue, sent, win, doc, store };
}

const http = (status: number, retryAfterMs?: number) =>
  new DleError('http', `HTTP ${status}`, { status, retriable: status === 429 || status >= 500, retryAfterMs });

describe('offline event queue (FR-226)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-09-03T10:00:00Z'));
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('timestamps events, persists them and flushes after the debounce', async () => {
    const { queue, sent, store } = harness();
    queue.enqueue({ type: 'session' });
    expect(queue.size()).toBe(1);
    expect(JSON.parse(store.get(QUEUE_KEY)!)).toEqual([{ type: 'session', ts: '2026-09-03T10:00:00.000Z' }]);
    expect(sent).toHaveLength(0);

    await vi.advanceTimersByTimeAsync(1000);

    expect(sent).toHaveLength(1);
    expect(sent[0]!.keepalive).toBe(false);
    expect(queue.size()).toBe(0);
    expect(store.get(QUEUE_KEY)).toBeNull();
  });

  it('keeps an explicit ts', () => {
    const { queue, store } = harness();
    queue.enqueue({ type: 'link_open', ts: '2026-09-03T09:00:00+00:00' });
    expect(JSON.parse(store.get(QUEUE_KEY)!)[0].ts).toBe('2026-09-03T09:00:00+00:00');
  });

  it('splits into batches of at most 100 in order', async () => {
    const { queue, sent } = harness(undefined, undefined, { maxSize: 1000 });
    for (let i = 0; i < 250; i += 1) queue.enqueue({ type: 'custom', name: String(i) });
    expect(await queue.flush()).toBe(true);
    expect(sent.map((s) => s.events.length)).toEqual([100, 100, 50]);
    expect(sent[0]!.events[0]!.name).toBe('0');
    expect(sent[2]!.events[49]!.name).toBe('249');
  });

  it('keeps events on a retriable failure and retries with exponential back-off', async () => {
    let failures = 2;
    const { queue, sent } = harness(undefined, () => (failures-- > 0 ? Promise.reject(http(503)) : Promise.resolve()));
    queue.enqueue({ type: 'session' });

    await vi.advanceTimersByTimeAsync(1000); // debounce -> attempt 1 fails
    expect(sent).toHaveLength(1);
    expect(queue.size()).toBe(1);

    await vi.advanceTimersByTimeAsync(999);
    expect(sent).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(1); // +1 s -> attempt 2 fails
    expect(sent).toHaveLength(2);

    await vi.advanceTimersByTimeAsync(1999);
    expect(sent).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1); // +2 s -> attempt 3 succeeds
    expect(sent).toHaveLength(3);
    expect(queue.size()).toBe(0);
  });

  it('honours Retry-After from a 429', async () => {
    let first = true;
    const { queue, sent } = harness(undefined, () => {
      if (first) {
        first = false;
        return Promise.reject(http(429, 30_000));
      }
      return Promise.resolve();
    });
    queue.enqueue({ type: 'session' });
    await vi.advanceTimersByTimeAsync(1000);
    expect(sent).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(29_000);
    expect(sent).toHaveLength(1);
    await queue.flush(); // an explicit flush inside the back-off window does not send either
    expect(sent).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(1000);
    expect(sent).toHaveLength(2);
    expect(queue.size()).toBe(0);
  });

  it('drops a batch the server refuses permanently and continues with the next', async () => {
    const { queue, sent } = harness(undefined, (_call, index) => (index === 0 ? Promise.reject(http(400)) : Promise.resolve()));
    for (let i = 0; i < 150; i += 1) queue.enqueue({ type: 'session' });
    expect(await queue.flush()).toBe(true);
    expect(sent).toHaveLength(2);
    expect(queue.size()).toBe(0);
  });

  it('flushes one keepalive batch on pagehide and empties it optimistically', async () => {
    const { queue, sent, win, store } = harness();
    for (let i = 0; i < 120; i += 1) queue.enqueue({ type: 'session' });

    win.dispatch('pagehide');
    await Promise.resolve();

    expect(sent).toHaveLength(1);
    expect(sent[0]!.keepalive).toBe(true);
    expect(sent[0]!.events).toHaveLength(100);
    // The remainder stays persisted for the next page load.
    expect(queue.size()).toBe(20);
    expect(JSON.parse(store.get(QUEUE_KEY)!)).toHaveLength(20);
  });

  it('treats a hidden tab like pagehide', async () => {
    const { queue, sent, doc } = harness();
    queue.enqueue({ type: 'session' });
    doc.visibilityState = 'hidden';
    doc.dispatch('visibilitychange');
    await Promise.resolve();
    expect(sent).toHaveLength(1);
    expect(sent[0]!.keepalive).toBe(true);
  });

  it('restores a persisted queue on the next load and flushes it', async () => {
    const store = createMemoryStore('t');
    const first = harness(store);
    first.queue.enqueue({ type: 'conversion', name: 'purchase', value: 24.9, currency: 'EUR' });
    first.queue.destroy();

    const second = harness(store);
    expect(second.queue.size()).toBe(1);
    await vi.advanceTimersByTimeAsync(1000);
    expect(second.sent).toHaveLength(1);
    expect(second.sent[0]!.events[0]).toMatchObject({ type: 'conversion', name: 'purchase', value: 24.9 });
  });

  it('prunes events the server would reject as stale (30 days) and garbage', () => {
    const store = createMemoryStore('t');
    store.set(
      QUEUE_KEY,
      JSON.stringify([
        { type: 'session', ts: '2026-08-03T09:00:00Z' }, // 31 days old
        { type: 'session', ts: '2026-08-06T10:00:00Z' }, // 28 days old
        { type: 'bogus', ts: '2026-09-03T09:00:00Z' },
        'not an event',
      ]),
    );
    const { queue } = harness(store);
    expect(queue.size()).toBe(1);
    expect(JSON.parse(store.get(QUEUE_KEY)!)).toEqual([{ type: 'session', ts: '2026-08-06T10:00:00Z' }]);
  });

  it('drops the oldest events beyond maxSize', () => {
    const { queue } = harness(undefined, undefined, { maxSize: 3 });
    for (let i = 0; i < 5; i += 1) queue.enqueue({ type: 'custom', name: String(i) });
    expect(queue.size()).toBe(3);
  });

  it('discards the queue instead of flushing when consent is withdrawn', async () => {
    let allowed = true;
    const { queue, sent, store } = harness(undefined, undefined, { mayFlush: () => allowed });
    queue.enqueue({ type: 'session' });
    allowed = false;
    expect(await queue.flush()).toBe(true);
    expect(sent).toHaveLength(0);
    expect(queue.size()).toBe(0);
    expect(store.get(QUEUE_KEY)).toBeNull();
  });

  it('waits while offline and sends when the browser comes back online', async () => {
    const { queue, sent, win } = harness();
    win.navigator.onLine = false;
    queue.enqueue({ type: 'session' });
    await vi.advanceTimersByTimeAsync(1000);
    expect(sent).toHaveLength(0);
    expect(queue.size()).toBe(1);

    win.navigator.onLine = true;
    win.dispatch('online');
    await vi.advanceTimersByTimeAsync(0);
    expect(sent).toHaveLength(1);
  });

  it('coalesces concurrent flushes into one in-flight request', async () => {
    let release: () => void = () => undefined;
    const { queue, sent } = harness(undefined, () => new Promise<void>((resolve) => (release = resolve)));
    queue.enqueue({ type: 'session' });
    const a = queue.flush();
    const b = queue.flush();
    expect(sent).toHaveLength(1);
    release();
    expect(await Promise.all([a, b])).toEqual([true, true]);
  });

  it('destroy() removes listeners and stops the timer', async () => {
    const { queue, sent, win, doc } = harness();
    queue.enqueue({ type: 'session' });
    queue.destroy();
    await vi.advanceTimersByTimeAsync(5000);
    win.dispatch('pagehide');
    expect(sent).toHaveLength(0);
    expect(win.count('pagehide')).toBe(0);
    expect(win.count('online')).toBe(0);
    expect(doc.count('visibilitychange')).toBe(0);
  });
});
