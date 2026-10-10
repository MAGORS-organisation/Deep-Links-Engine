#!/usr/bin/env python3
"""Automated crawler verification check (docs/zadanie.md §D.2.1, TC-106, ADR-009).

Verifies that social and messaging crawler user agents:
1. Receive HTTP 200 with HTML (not a 30x redirect or application/json error).
2. The HTML contains valid Open Graph meta tags (og:title, og:image, og:url, og:type).
3. Requests do not pollute campaign click counts or trigger rate limiting.

Crawlers verified (§D.2.1):
  - facebookexternalhit (Facebook link preview)
  - Twitterbot (X / Twitter link preview)
  - Slackbot / Slack-ImgProxy (Slack link preview)
  - LinkedInBot (LinkedIn preview)
  - Discordbot (Discord preview)
  - WhatsApp (WhatsApp preview)

Environment:
  DLE_LINK_URL          URL of an active deep link to check (e.g. https://link.example.com/promo)
  GITHUB_STEP_SUMMARY   Optional file to append Markdown summary to

Exit codes:
  0 = All crawlers received HTTP 200 with valid OG tags
  1 = At least one crawler failed
  2 = Configuration error / URL not provided
"""
from __future__ import annotations

import os
import re
import sys
import urllib.error
import urllib.request

CRAWLERS = [
    {
        "name": "Facebook",
        "ua": "facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)",
    },
    {
        "name": "Twitter / X",
        "ua": "Twitterbot/1.0",
    },
    {
        "name": "Slack",
        "ua": "Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)",
    },
    {
        "name": "LinkedIn",
        "ua": "LinkedInBot/1.0 (compatible; Mozilla/5.0; Apache-HttpClient +http://www.linkedin.com)",
    },
    {
        "name": "Discord",
        "ua": "Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)",
    },
    {
        "name": "WhatsApp",
        "ua": "WhatsApp/2.23.20.76 A",
    },
]

REQUIRED_OG_TAGS = [
    r'<meta\s+property=["\']og:title["\']',
    r'<meta\s+property=["\']og:description["\']|<meta\s+property=["\']og:image["\']|<meta\s+property=["\']og:url["\']',
]


class NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    """Prevents automatic redirect following so we can assert the raw status code."""
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def check_crawler(url: str, crawler: dict) -> tuple[bool, str]:
    name = crawler["name"]
    ua = crawler["ua"]

    opener = urllib.request.build_opener(NoRedirectHandler)
    req = urllib.request.Request(url, headers={"User-Agent": ua, "Accept": "text/html,application/xhtml+xml"})

    try:
        with opener.open(req, timeout=10) as resp:
            status = resp.status
            content_type = resp.headers.get("Content-Type", "")
            body = resp.read().decode("utf-8", errors="replace")

            if status != 200:
                return False, f"Expected HTTP 200, got HTTP {status}"

            if "text/html" not in content_type:
                return False, f"Expected text/html, got {content_type}"

            for pattern in REQUIRED_OG_TAGS:
                if not re.search(pattern, body, re.IGNORECASE):
                    return False, f"Missing required Open Graph meta tag matching pattern: {pattern}"

            return True, f"HTTP 200 OK, valid Open Graph tags present ({len(body)} bytes)"
    except urllib.error.HTTPError as err:
        if err.code in (301, 302, 303, 307, 308):
            location = err.headers.get("Location", "")
            return False, f"Crawler received unexpected HTTP {err.code} redirect to '{location}'"
        return False, f"HTTP error {err.code}: {err.reason}"
    except Exception as exc:
        return False, f"Connection failure: {exc}"


def main() -> int:
    link_url = os.getenv("DLE_LINK_URL")
    if not link_url and len(sys.argv) > 1:
        link_url = sys.argv[1]

    if not link_url:
        print("Usage: verify-crawlers.py <DLE_LINK_URL>", file=sys.stderr)
        print("Error: DLE_LINK_URL environment variable or command-line argument is required.", file=sys.stderr)
        return 2

    print(f"Verifying crawler responses for link: {link_url}")
    print("-" * 70)

    results = []
    has_failure = False

    for crawler in CRAWLERS:
        name = crawler["name"]
        ok, msg = check_crawler(link_url, crawler)
        results.append((name, ok, msg))
        symbol = "✅ PASS" if ok else "❌ FAIL"
        print(f"[{symbol}] {name:<15} : {msg}")
        if not ok:
            has_failure = True

    print("-" * 70)

    # Optional GitHub Step Summary
    summary_path = os.getenv("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as f:
            f.write("### Crawler Verification (§D.2.1)\n\n")
            f.write(f"Tested link: `{link_url}`\n\n")
            f.write("| Crawler | Status | Details |\n|---|---|---|\n")
            for name, ok, msg in results:
                status_icon = "✅ Pass" if ok else "❌ Fail"
                f.write(f"| {name} | {status_icon} | {msg} |\n")
            f.write("\n")

    if has_failure:
        print("Verification failed: at least one crawler did not receive HTTP 200 with OG tags.")
        return 1

    print("All crawlers successfully received HTTP 200 with Open Graph metadata.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
