# Webhooks

**What this is:** the postback contract of [§B.7.4](../zadanie.md#b74-webhook-postback) — headers, payload, the dual signature and how to verify it in C#, Node and Python — plus replay protection, retries, the dead-letter queue, JWKS and the reserved post-quantum slot.
**Who it is for:** the developer receiving `attribution.created` on their server, and the reviewer checking that the receiver verifies before it trusts.

## Subscribing

```bash
curl -s -X POST https://go.example.com/api/v1/webhooks \
  -H "Authorization: Bearer dle_…" -H "Content-Type: application/json" \
  -d '{"url":"https://hooks.example.com/dle","event_types":["attribution.created","link.quarantined"],"is_active":true}'
# → {"id":"…","url":"…","event_types":[…],"is_active":true,"created_at":"…",
#    "secret":"<base64, 32 bytes>",   ← shown once; store it. The HMAC key is these bytes, base64-decoded
#    "signing_key_id":"whk-7Hq2mZ0cX4bA"}    ← the kid whose public key is in /.well-known/jwks.json
curl -s -X POST https://go.example.com/api/v1/webhooks/<ID>/test -H "Authorization: Bearer dle_…"
curl -s "https://go.example.com/api/v1/webhooks/deliveries?subscriptionId=<ID>" -H "Authorization: Bearer dle_…"
```

Destinations must be public `https` hosts; private and link-local addresses are refused unless `Dle:Webhooks:AllowPrivateDestinations` is set (the same SSRF policy as link targets, T-02). Up to 20 subscriptions per tenant.

### Event types

| Event | When | `data` carries |
|---|---|---|
| `attribution.created` | `POST /v1/resolve` produced a match (any `match_type` except `none`) | `install_id`, `click_id`, `link_id`, `match_type`, `confidence`, `platform`, campaign / UTM keys |
| `link.created`, `link.updated`, `link.archived`, `link.deleted`, `link.bulk_imported` | Link lifecycle | `link_id`, `slug`, `host`, `version` |
| `link.quarantined`, `link.released` | Abuse pipeline decisions | `link_id`, `reason` |
| `domain.created`, `domain.updated`, `domain.deleted`, `domain.verified`, `domain.verification_failed` | Domain lifecycle and the nightly verifier | `domain_id`, `host`, failing check codes |
| `webhook.created`, `webhook.deactivated`, `webhook.test` | Subscription lifecycle; `test` is what `POST /webhooks/{id}/test` sends | `subscription_id` |

Payload values are pseudonymous by design (a `click_id`, never an IP); what a receiver can re-identify decides whether it holds personal data ([privacy.md](../compliance/privacy.md#the-legal-frame-the-design-rests-on)).

## The delivery

```http
POST /hooks/dle HTTP/1.1
Host: hooks.example.com
Content-Type: application/json
DLE-Signature: t=1756900000, v1=<base64 HMAC-SHA256>, v2=<base64 Ed25519>, kid=whk-7Hq2mZ0cX4bA
DLE-Alg: HS256+Ed25519
User-Agent: dle-webhooks/1

{"event":"attribution.created","id":"01J…","occurred_at":"2026-09-11T10:00:00Z","data":{"install_id":"…","click_id":"aB3xK9pQ","link_id":"…","match_type":"install_referrer","confidence":"1.0","platform":"android"}}
```

| Member | Meaning |
|---|---|
| `t` | Unix seconds when the delivery was signed |
| `v1` | Base64 (standard alphabet, padded) of `HMAC-SHA256(secret, signing_input)` — the symmetric slot; `secret` is the subscription secret **base64-decoded** (the 32 raw bytes, not the string you were shown) |
| `v2` | Base64 of `Ed25519_sign(signing_input)` with the instance's webhook key — the asymmetric slot, verifiable by a third party without the secret; **omitted** when the deployment publishes no webhook key |
| `kid` | Identifier of the key behind `v2`, resolvable in JWKS |
| `DLE-Alg` | Algorithms in use, e.g. `HS256+Ed25519`; a future `+MLDSA65` announces the `v3` slot |

**Signing input** for both slots is the byte string `t + "." + body` — the decimal timestamp, an ASCII period, and the **exact bytes of the request body**. Do not re-serialise the JSON before verifying; hash what arrived.

## Verification — what a correct receiver does

1. Parse `DLE-Signature` into `t`, `v1`, `v2`, `kid`. Malformed → reject.
2. Reject if `|now − t| > 5 minutes` (`Dle:Webhooks:SignatureToleranceMinutes`).
3. Build `signing_input = t + "." + raw_body_bytes`.
4. Verify `v1` against your secret with a **constant-time** comparison. Verify `v2` against the JWKS key for `kid` when present. Require at least `v1`; require both if you have the public key.
5. Replay protection: the payload `id` is unique per event — keep the ids you have seen for at least the tolerance window (5 minutes; 24 hours is a reasonable store) and drop duplicates. Retries re-send the **same** `id` with a **new** `t` and fresh signatures.
6. Answer `2xx` quickly (under 10 s) and process asynchronously. Any non-2xx or a timeout is retried.

### C#

```csharp
using System.Security.Cryptography;
using System.Text;

public static bool VerifyDleWebhook(
    string signatureHeader, ReadOnlySpan<byte> body, byte[] secret,   // Convert.FromBase64String(stored secret)
    Func<string, byte[]?> ed25519PublicKeyByKid, TimeProvider clock)
{
    var parts = signatureHeader.Split(',', StringSplitOptions.TrimEntries)
        .Select(p => p.Split('=', 2)).Where(kv => kv.Length == 2)
        .ToDictionary(kv => kv[0], kv => kv[1]);
    if (!parts.TryGetValue("t", out var tStr) || !long.TryParse(tStr, out var t)) return false;
    if (!parts.TryGetValue("v1", out var v1)) return false;
    if (Math.Abs(clock.GetUtcNow().ToUnixTimeSeconds() - t) > 300) return false;   // 5-minute tolerance

    var input = new byte[Encoding.ASCII.GetByteCount(tStr) + 1 + body.Length];
    var n = Encoding.ASCII.GetBytes(tStr, input);
    input[n] = (byte)'.';
    body.CopyTo(input.AsSpan(n + 1));

    var expected = HMACSHA256.HashData(secret, input);
    if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(v1))) return false;

    if (parts.TryGetValue("v2", out var v2) && parts.TryGetValue("kid", out var kid))
    {
        var pub = ed25519PublicKeyByKid(kid);          // 32 bytes from JWKS: kty OKP, crv Ed25519, x (base64url)
        if (pub is null) return false;
        // Ed25519 verification: NSec, BouncyCastle, or the platform's implementation.
        if (!Ed25519Verify(pub, input, Convert.FromBase64String(v2))) return false;
    }
    return true;
}
```

### Node (18+)

```js
import crypto from "node:crypto";

export function verifyDleWebhook(headerValue, rawBody, secret, jwks, now = Date.now()) {
  const p = Object.fromEntries(headerValue.split(",").map(s => s.trim().split(/=(.*)/s).slice(0, 2)));
  if (!p.t || !p.v1) return false;
  if (Math.abs(now / 1000 - Number(p.t)) > 300) return false;

  const input = Buffer.concat([Buffer.from(`${p.t}.`, "ascii"), rawBody]);   // rawBody: Buffer, not re-stringified JSON
  const v1 = crypto.createHmac("sha256", secret).update(input).digest();   // secret: Buffer.from(storedSecret, "base64")
  const got = Buffer.from(p.v1, "base64");
  if (v1.length !== got.length || !crypto.timingSafeEqual(v1, got)) return false;

  if (p.v2 && p.kid) {
    const jwk = jwks.keys.find(k => k.kid === p.kid && k.kty === "OKP" && k.crv === "Ed25519");
    if (!jwk) return false;
    const key = crypto.createPublicKey({ key: jwk, format: "jwk" });
    if (!crypto.verify(null, input, key, Buffer.from(p.v2, "base64"))) return false;
  }
  return true;
}
// Express: app.post("/hooks/dle", express.raw({ type: "application/json" }), (req, res) => { … req.body is a Buffer … })
```

### Python (3.10+, `cryptography`)

```python
import base64, hmac, hashlib, time
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
from cryptography.exceptions import InvalidSignature

def verify_dle_webhook(header: str, raw_body: bytes, secret: bytes, jwks: dict, now: float | None = None) -> bool:
    parts = dict(kv.strip().split("=", 1) for kv in header.split(",") if "=" in kv)
    t, v1 = parts.get("t"), parts.get("v1")
    if not t or not v1:
        return False
    if abs((now or time.time()) - int(t)) > 300:
        return False
    signing_input = t.encode("ascii") + b"." + raw_body          # raw bytes as received
    expected = hmac.new(secret, signing_input, hashlib.sha256).digest()   # secret = base64.b64decode(stored_secret)
    if not hmac.compare_digest(expected, base64.b64decode(v1)):
        return False
    v2, kid = parts.get("v2"), parts.get("kid")
    if v2 and kid:
        jwk = next((k for k in jwks["keys"] if k.get("kid") == kid and k.get("crv") == "Ed25519"), None)
        if not jwk:
            return False
        pub = Ed25519PublicKey.from_public_bytes(base64.urlsafe_b64decode(jwk["x"] + "=="))
        try:
            pub.verify(base64.b64decode(v2), signing_input)
        except InvalidSignature:
            return False
    return True
```

## JWKS

`GET https://go.example.com/.well-known/jwks.json` (control plane, anonymous, cacheable):

```json
{ "keys": [ { "kty": "OKP", "crv": "Ed25519", "kid": "whk-7Hq2mZ0cX4bA", "alg": "Ed25519", "use": "sig", "x": "<base64url 32 bytes>" } ] }
```

The key does not rotate. Automatic key rotation is not implemented, and `Dle:Crypto:KeyRotationDays` does not apply to this key ([Known gaps](../../README.md#known-gaps)). The Ed25519 key and its `kid` (`whk-…`) are derived from the instance's master secret (`Dle:Crypto:MasterSecret`), so every replica signs with the same key and it survives restarts for as long as that secret stays the same. Changing the master secret changes the key and the `kid` at once, **with no overlap**: JWKS stops listing the old key immediately, and the subscription secrets stored under the old master secret can no longer be decrypted, so deliveries to those subscriptions fail until they are recreated with a new secret. Cache the document for at most an hour and re-fetch on an unknown `kid` before rejecting.

## Retries, backoff, dead-letter queue

| Setting (`Dle:Webhooks:*`) | Default | Meaning |
|---|---|---|
| `MaxAttempts` | 8 | Attempts before dead-lettering |
| `BaseDelaySeconds` | 5 | First retry delay; each attempt doubles: 5, 10, 20, 40, 80, 160, 320, 640 s (≈ 21 minutes in total) |
| `MaxDelaySeconds` | 3600 | Cap on a single delay |
| `JitterRatio` | 0.2 | ±20 % randomisation to avoid thundering herds |
| `TimeoutSeconds` / `ConnectTimeoutSeconds` | 10 / 5 | Per attempt |
| `BatchSize`, `PollIntervalSeconds` | 50 / 5 | Outbox worker cadence |

What counts as success: any `2xx`. `3xx` is **not** followed. `4xx` other than `408` and `429` is retried too — a receiver that is temporarily misconfigured should not lose events — until `MaxAttempts` is spent.

After the last attempt the delivery is **dead-lettered**: it stays in `GET /api/v1/webhooks/deliveries` with `status: dead`, `attempt`, `response_code`, `last_error` and (with `includePayload=true`) the full `payload`, and the instance-wide gauge `dle_webhook_dlq_size` goes above zero — which deserves an alert, though no alert rule ships ([§C.6](../zadanie.md#c6-pozorovateľnosť), [operations/runbook.md](../operations/runbook.md), [Known gaps](../../README.md#known-gaps)). There is no replay endpoint and no console action: a dead-lettered delivery is not sent again, and recovering it means working from the stored payload.

Delivery is **at-least-once**; ordering is per subscription best-effort, not guaranteed across retries. Idempotent processing keyed on the event `id` is the receiver's job.

## The reserved `v3` slot — ML-DSA-65

The two-slot design is the post-quantum preparation the specification asks for ([§E.5](../zadanie.md#e5-post-quantum-architektúra)): `v1` is quantum-safe already (symmetric), `v2` is not (Ed25519 falls to Shor). Phase 1 of the migration plan adds `v3=<base64 ML-DSA-65>` over the **same signing input**, `DLE-Alg: HS256+Ed25519+MLDSA65`, and a JWKS entry with the ML-DSA public key under its own `kid`. Receivers that ignore `v3` keep working — an unknown member is ignored by construction — and receivers that want post-quantum assurance verify `v2` **and** `v3` (hybrid), never `v3` alone until the algorithm is out of experimental status in the platforms. Write your parser to tolerate members it does not know; the three examples above do.

Related: [api.md](api.md) · [compliance/security-overview.md](../compliance/security-overview.md) (T-13, K4).
