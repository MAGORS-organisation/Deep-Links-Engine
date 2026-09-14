/**
 * Randomness for the two things the SDK ever generates: the install id and trace ids.
 *
 * Everything here is **random**, never derived. No canvas hash, no font list, no `navigator`
 * enumeration, no clock skew — nothing that would survive a storage wipe or re-identify a
 * device (FR-227, spec §0.2).
 */

interface CryptoLike {
  getRandomValues?: (array: Uint8Array) => Uint8Array;
  randomUUID?: () => string;
}

export function cryptoObject(): CryptoLike | undefined {
  return (globalThis as { crypto?: CryptoLike }).crypto;
}

/** `length` random bytes, never all zero. */
export function randomBytes(length: number): Uint8Array {
  const bytes = new Uint8Array(length);
  const c = cryptoObject();
  try {
    if (typeof c?.getRandomValues === 'function') c.getRandomValues(bytes);
  } catch {
    /* fall through to the weaker source below */
  }
  if (bytes.every((b) => b === 0)) {
    // Last resort for hosts without Web Crypto. Weaker, but still random — still not a fingerprint.
    for (let i = 0; i < length; i += 1) bytes[i] = Math.floor(Math.random() * 256);
    bytes[length - 1] ||= 1;
  }
  return bytes;
}

/** Lower-case hex of a byte array. */
export function hex(bytes: Uint8Array): string {
  let out = '';
  for (const b of bytes) out += (b < 16 ? '0' : '') + b.toString(16);
  return out;
}
