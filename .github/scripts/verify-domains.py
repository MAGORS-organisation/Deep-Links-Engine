#!/usr/bin/env python3
"""Nightly re-verification of every registered domain's AASA / assetlinks (docs/zadanie.md §C.8
"nightly", §D.7 criterion 7).

Talks to the control plane only through its public API:

  GET  /api/v1/domains                      list (paged)
  POST /api/v1/domains/{id}/verify          run the verifier now (same as the console button)

Both need an API key with rights on the tenant whose domains are to be checked; an instance-wide
sweep therefore needs a key on the instance tenant (Dle:Control:InstanceTenantId).

Environment:
  DLE_CONTROL_URL   e.g. https://control.example.com      (required)
  DLE_API_KEY       Bearer key                             (required)
  DLE_PAGE_SIZE     default 200 (Dle:Control:MaxPageSize)
  GITHUB_STEP_SUMMARY  appended to when present

Exit codes: 0 all domains verified, 1 at least one failed, 2 configuration / transport error.

The list and verify response shapes are read defensively (an `items` array or a bare array; a
`verified`/`ok`/`success` boolean or a `status` string) because this script must keep working
across control-plane releases. Unexpected shapes are printed verbatim so the run is debuggable.
"""
from __future__ import annotations

import json
import os
import sys
import urllib.error
import urllib.parse
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
            return response.status, None
        try:
            return response.status, json.loads(raw)
        except json.JSONDecodeError:
            return response.status, raw.decode("utf-8", errors="replace")


def list_domains(base: str, key: str, page_size: int) -> list[dict]:
    domains: list[dict] = []
    page = 1
    while True:
        query = urllib.parse.urlencode({"page": page, "pageSize": page_size})
        _status, payload = request("GET", f"{base}/api/v1/domains?{query}", key)
        if isinstance(payload, list):
            items, more = payload, False
        elif isinstance(payload, dict):
            items = payload.get("items") or payload.get("data") or payload.get("domains") or []
            total = payload.get("totalCount") or payload.get("total")
            more = bool(payload.get("nextCursor") or payload.get("next")) or (
                isinstance(total, int) and page * page_size < total)
        else:
            print(f"::error::unexpected domain list payload: {payload!r}")
            return domains
        domains.extend(items)
        if not more or not items:
            return domains
        page += 1


def outcome(payload) -> tuple[bool | None, str]:
    """(verified?, human readable status) from a verify response of unknown shape."""
    if isinstance(payload, dict):
        for flag in ("verified", "isVerified", "ok", "success"):
            if isinstance(payload.get(flag), bool):
                return payload[flag], json.dumps(payload, ensure_ascii=False)[:400]
        status = payload.get("status") or payload.get("state") or payload.get("result")
        if isinstance(status, str):
            return status.lower() in {"verified", "ok", "passed", "success", "succeeded"}, status
        return None, json.dumps(payload, ensure_ascii=False)[:400]
    return None, repr(payload)[:400]


def main() -> int:
    base = os.environ.get("DLE_CONTROL_URL", "").rstrip("/")
    key = os.environ.get("DLE_API_KEY", "")
    if not base or not key:
        print("::error::DLE_CONTROL_URL and DLE_API_KEY are required")
        return 2
    page_size = int(os.environ.get("DLE_PAGE_SIZE", "200"))

    try:
        domains = list_domains(base, key, page_size)
    except (urllib.error.URLError, urllib.error.HTTPError, OSError) as error:
        print(f"::error::could not list domains at {base}: {error}")
        return 2

    rows: list[str] = []
    failures = 0
    unknown = 0
    for domain in domains:
        domain_id = domain.get("id")
        host = domain.get("host") or domain.get("hostname") or domain.get("name") or "?"
        if not domain_id:
            print(f"::warning::domain without id skipped: {domain!r}")
            unknown += 1
            continue
        try:
            _status, payload = request("POST", f"{base}/api/v1/domains/{domain_id}/verify", key, {})
            verified, detail = outcome(payload)
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
        rows.append(f"| `{host}` | `{domain_id}` | {label} | {detail.replace('|', '\\|')} |")
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
        return 1
    if unknown:
        print(f"::warning::{unknown} domain(s) returned a response this script could not classify")
    return 0


if __name__ == "__main__":
    sys.exit(main())
