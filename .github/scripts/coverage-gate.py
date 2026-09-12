#!/usr/bin/env python3
"""Coverage threshold gate for the Deep Link Engine CI (docs/zadanie.md §D.1).

Reads a merged Cobertura report (dotnet-coverage output), computes line coverage per assembly and
for the product as a whole, and enforces:

  * domain tier  (Dle.Domain, Dle.Crypto)  >= --domain-threshold   -> FAIL when missed
  * overall      (every Dle.* assembly that is not a test assembly) >= --overall-threshold
                                                                    -> WARN or FAIL per --overall-mode

Third-party assemblies that dynamic instrumentation happens to see (StackExchange.Redis, BouncyCastle,
...) are ignored: they are not our lines. Test assemblies (Dle.*Tests) are ignored for the same reason.

Usage:
  coverage-gate.py merged.cobertura.xml [--domain-threshold 90] [--overall-threshold 70]
                   [--domain-assemblies Dle.Domain,Dle.Crypto] [--overall-mode warn|fail]
                   [--summary $GITHUB_STEP_SUMMARY]
"""
from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("report", help="merged Cobertura XML")
    p.add_argument("--domain-threshold", type=float, default=90.0)
    p.add_argument("--overall-threshold", type=float, default=70.0)
    p.add_argument("--domain-assemblies", default="Dle.Domain,Dle.Crypto")
    p.add_argument("--overall-mode", choices=("warn", "fail"), default="warn")
    p.add_argument("--product-prefix", default="Dle.")
    p.add_argument("--summary", help="file to append a Markdown summary to (GITHUB_STEP_SUMMARY)")
    return p.parse_args()


def line_counts(package: ET.Element) -> tuple[int, int]:
    """(covered, valid) for a Cobertura <package>, de-duplicated by (file, line)."""
    seen: dict[tuple[str, int], bool] = {}
    for cls in package.iter("class"):
        filename = cls.get("filename", "")
        if filename.endswith((".g.cs", ".generated.cs", ".Designer.cs")):
            # Source-generated code (System.Text.Json contexts, LoggerMessage) is not a coverage
            # target; counting it halves Dle.Domain's figure without saying anything about the tests.
            continue
        for line in cls.iter("line"):
            number = int(line.get("number", "0"))
            hit = int(line.get("hits", "0")) > 0
            key = (filename, number)
            seen[key] = seen.get(key, False) or hit
    covered = sum(1 for hit in seen.values() if hit)
    return covered, len(seen)


def pct(covered: int, valid: int) -> float:
    return 100.0 * covered / valid if valid else 0.0


def main() -> int:
    args = parse_args()
    root = ET.parse(args.report).getroot()
    domain = {name.strip() for name in args.domain_assemblies.split(",") if name.strip()}

    per_assembly: dict[str, list[int]] = defaultdict(lambda: [0, 0])
    for package in root.iter("package"):
        name = package.get("name", "")
        if not name.startswith(args.product_prefix) or name.endswith("Tests"):
            continue
        covered, valid = line_counts(package)
        per_assembly[name][0] += covered
        per_assembly[name][1] += valid

    if not per_assembly:
        print(f"::error::no {args.product_prefix}* assemblies found in {args.report}")
        return 2

    failures: list[str] = []
    warnings: list[str] = []
    rows: list[str] = []

    for name in sorted(per_assembly):
        covered, valid = per_assembly[name]
        value = pct(covered, valid)
        if name in domain:
            ok = value >= args.domain_threshold
            gate = f"domain >= {args.domain_threshold:.0f}%"
            if not ok:
                failures.append(f"{name}: {value:.1f}% < {args.domain_threshold:.0f}% (domain tier, section D.1)")
        else:
            ok = True
            gate = "counts toward overall"
        rows.append(f"| `{name}` | {covered} / {valid} | {value:.1f}% | {gate} | {'pass' if ok else '**FAIL**'} |")

    total_covered = sum(v[0] for v in per_assembly.values())
    total_valid = sum(v[1] for v in per_assembly.values())
    overall = pct(total_covered, total_valid)
    overall_ok = overall >= args.overall_threshold
    if not overall_ok:
        message = f"overall: {overall:.1f}% < {args.overall_threshold:.0f}% (section D.1)"
        (failures if args.overall_mode == "fail" else warnings).append(message)

    missing = sorted(domain - set(per_assembly))
    for name in missing:
        failures.append(f"{name}: not present in the report at all")

    summary = [
        "## Code coverage (line)",
        "",
        "| Assembly | Lines | Coverage | Gate | Result |",
        "|---|---:|---:|---|---|",
        *rows,
        f"| **overall (product)** | {total_covered} / {total_valid} | **{overall:.1f}%** | "
        f">= {args.overall_threshold:.0f}% ({args.overall_mode}) | "
        f"{'pass' if overall_ok else ('**FAIL**' if args.overall_mode == 'fail' else 'warn')} |",
        "",
    ]
    if warnings:
        summary += ["> [!WARNING]", *(f"> {w}" for w in warnings), ""]
    if failures:
        summary += ["> [!CAUTION]", *(f"> {f}" for f in failures), ""]

    text = "\n".join(summary)
    print(text)
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as handle:
            handle.write(text + "\n")

    for w in warnings:
        print(f"::warning title=Coverage below target::{w}")
    for f in failures:
        print(f"::error title=Coverage gate::{f}")

    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
