#!/usr/bin/env python3
"""Print a Markdown summary of a TRX (Visual Studio test results) file for GITHUB_STEP_SUMMARY.

xunit.v3 on Microsoft.Testing.Platform writes TRX through `-result-trx <file>`. This script only
reads the <Counters> element and the names of failed tests, so it stays useful when a suite has a
thousand tests.

Usage:
  trx-summary.py results.trx --name Dle.UnitTests
Exit code is 0 even when tests failed: the test run itself already failed the step.
"""
from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trx")
    parser.add_argument("--name", default=None)
    parser.add_argument("--max-failures", type=int, default=25)
    args = parser.parse_args()
    name = args.name or args.trx

    try:
        root = ET.parse(args.trx).getroot()
    except (OSError, ET.ParseError) as error:
        print(f"### {name}\n\nNo TRX file could be read ({error}). The suite probably did not start.\n")
        return 0

    counters = root.find("t:ResultSummary/t:Counters", NS)
    outcome = root.find("t:ResultSummary", NS)
    if counters is None:
        print(f"### {name}\n\nTRX has no ResultSummary/Counters element.\n")
        return 0

    def count(attr: str) -> int:
        return int(counters.get(attr, "0") or 0)

    total, passed, failed = count("total"), count("passed"), count("failed")
    skipped = count("notExecuted") + count("inconclusive")
    status = "pass" if failed == 0 and (outcome is None or outcome.get("outcome") != "Failed") else "**FAIL**"

    print(f"### {name}: {status}\n")
    print("| Total | Passed | Failed | Skipped |")
    print("|---:|---:|---:|---:|")
    print(f"| {total} | {passed} | {failed} | {skipped} |\n")

    failures = [
        result.get("testName", "?")
        for result in root.iterfind(".//t:UnitTestResult", NS)
        if result.get("outcome") == "Failed"
    ]
    if failures:
        print("<details><summary>Failed tests</summary>\n")
        for test in failures[: args.max_failures]:
            print(f"- `{test}`")
        if len(failures) > args.max_failures:
            print(f"- ... and {len(failures) - args.max_failures} more")
        print("\n</details>\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
