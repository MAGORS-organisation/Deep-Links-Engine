#!/usr/bin/env python3
"""Nightly re-verification of every registered domain's AASA / assetlinks.

Talks to the control plane only through its public API (snake_case JSON):

  GET  /api/v1/domains                      {"items": [DomainResponse...], "next_cursor": null, "total": n}
  POST /api/v1/domains/{id}/verify          DomainVerificationResponse, `ok` true when every check passed

Both need an API key with rights on the tenant whose domains are to be checked; an instance-wide
sweep therefore needs a key on the instance tenant (Dle:Control:InstanceTenantId).

Environment:
  DLE_CONTROL_URL   e.g. https://control.example.com      (required)
  DLE_API_KEY       Bearer key                             (required)
  GITHUB_STEP_SUMMARY  appended to when present

Exit codes: 0 all domains verified, 1 at least one failed or answered in a shape this script does
not know, 2 configuration / transport error. A response it cannot classify is never counted as
verified: a green night must mean every domain was actually checked.
"""
from __future__ import annotations

import json
import os
import sys
import urllib.error
import urllib.request

TIMEOUT_SECONDS = 60


def request(method: str, url: str, key: str, body: dict | None = None):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", f"Bearer {key}")
    req.add_header("Accept", "application/json")
    req.add_header("User-Agent", "dle-nightly-domain-verification/1")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=TIMEOUT_SECONDS) as response:  # noqa: S310 (URL comes from configuration)
        raw = response.read()
        if not raw:
            return None
        try:
            return json.loads(raw)
        except json.JSONDecodeError:
            return raw.decode("utf-8", errors="replace")


def list_domains(base: str, key: str) -> list[dict] | None:
    """Every domain of the key's tenant. The endpoint returns them all in one response."""
    payload = request("GET", f"{base}/api/v1/domains", key)
    if not isinstance(payload, dict) or not isinstance(payload.get("items"), list):
        print(f"::error::unexpected domain list payload: {payload!r}"[:600])
        return None
    if payload.get("next_cursor"):
        # The list became paged. Checking only the first page would report a partial sweep as green.
        print("::error::GET /api/v1/domains returned next_cursor; this script reads a single page and must learn to page")
        return None
    return payload["items"]


def outcome(payload) -> tuple[bool | None, str]:
    """(verified?, human readable detail) from a verify response; None when the shape is unknown."""
    if isinstance(payload, dict) and isinstance(payload.get("ok"), bool):
        failed = [c.get("kind", "?") for c in payload.get("checks") or [] if isinstance(c, dict) and c.get("status") == "failed"]
        detail = "ok" if payload["ok"] else "failed: " + (", ".join(failed) or "see the console")
        return payload["ok"], detail
    return None, repr(payload)[:400]


def main() -> int:
    base = os.environ.get("DLE_CONTROL_URL", "").rstrip("/")
    key = os.environ.get("DLE_API_KEY", "")
    if not base or not key:
        print("::error::DLE_CONTROL_URL and DLE_API_KEY are required")
        return 2

    try:
        domains = list_domains(base, key)
    except (urllib.error.URLError, urllib.error.HTTPError, OSError) as error:
        print(f"::error::could not list domains at {base}: {error}")
        return 2
    if domains is None:
        return 2

    rows: list[str] = []
    failures = 0
    unknown = 0
    for domain in domains:
        domain_id = domain.get("id")
        host = domain.get("host") or "?"
        if not domain_id:
            print(f"::warning::domain without id skipped: {domain!r}")
            unknown += 1
            continue
        try:
            verified, detail = outcome(request("POST", f"{base}/api/v1/domains/{domain_id}/verify", key, {}))
        except urllib.error.HTTPError as error:
            verified, detail = False, f"HTTP {error.code}"
        except (urllib.error.URLError, OSError) as error:
            verified, detail = False, str(error)
        if verified is None:
            unknown += 1
            label = "unknown"
        elif verified:
            label = "verified"
        else:
            failures += 1
            label = "**FAILED**"
        cell = detail.replace("|", "\\|")
        rows.append(f"| `{host}` | `{domain_id}` | {label} | {cell} |")
        print(f"{label:>10}  {host}  {detail}")

    summary = [
        "## Nightly domain verification (AASA / assetlinks)",
        "",
        f"{len(domains)} domain(s) checked against `{base}`: {len(domains) - failures - unknown} verified, "
        f"{failures} failed, {unknown} undetermined.",
        "",
        "| Host | Id | Result | Detail |",
        "|---|---|---|---|",
        *rows,
        "",
    ]
    text = "\n".join(summary)
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as handle:
            handle.write(text + "\n")
    else:
        print(text)

    if failures:
        print(f"::error::{failures} domain(s) failed AASA/assetlinks verification")
    if unknown:
        print(f"::error::{unknown} domain(s) could not be classified (no id, or a verify response without a boolean `ok`)")
    return 1 if failures or unknown else 0


if __name__ == "__main__":
    sys.exit(main())
