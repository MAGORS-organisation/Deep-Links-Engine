#!/usr/bin/env python3
"""Hard gate on known-vulnerable NuGet packages (docs/zadanie.md S-05, NFR-13, T-14).

Runs `dotnet list <solution> package --vulnerable --include-transitive --format json`, walks every
project / framework / top-level and transitive package, and:

  * fails (exit 1) when any package has a vulnerability at or above --fail-on (default High),
  * warns for anything below that,
  * writes a Markdown table to --summary when given (GITHUB_STEP_SUMMARY).

The audit source is the NuGet vulnerability database as consumed by the SDK; it needs network access
to the configured package sources.

Usage:
  nuget-vulnerabilities.py Dle.sln [--fail-on High] [--summary $GITHUB_STEP_SUMMARY]
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys

SEVERITY_ORDER = {"Low": 1, "Moderate": 2, "High": 3, "Critical": 4}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("solution")
    parser.add_argument("--fail-on", choices=list(SEVERITY_ORDER), default="High")
    parser.add_argument("--summary")
    args = parser.parse_args()

    command = ["dotnet", "list", args.solution, "package", "--vulnerable", "--include-transitive",
               "--format", "json"]
    completed = subprocess.run(command, capture_output=True, text=True, check=False)
    if completed.returncode != 0:
        print(completed.stdout)
        print(completed.stderr, file=sys.stderr)
        print(f"::error::`{' '.join(command)}` exited with {completed.returncode}")
        return 2

    # The SDK may print informational lines before the JSON document; take the first '{' onwards.
    payload = completed.stdout[completed.stdout.find("{"):]
    try:
        report = json.loads(payload)
    except json.JSONDecodeError as error:
        print(completed.stdout)
        print(f"::error::could not parse `dotnet list package` JSON output: {error}")
        return 2

    for problem in report.get("problems", []):
        level = problem.get("level", "warning").lower()
        text = problem.get("text", "")
        print(f"::{'error' if level == 'error' else 'warning'}::{text}")
        if level == "error":
            return 2

    findings: list[tuple[str, str, str, str, str, str]] = []
    for project in report.get("projects", []):
        project_path = project.get("path", "?")
        for framework in project.get("frameworks", []):
            tfm = framework.get("framework", "?")
            for kind in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(kind, []):
                    for vulnerability in package.get("vulnerabilities", []):
                        findings.append((
                            project_path,
                            tfm,
                            package.get("id", "?"),
                            package.get("resolvedVersion", "?"),
                            vulnerability.get("severity", "Unknown"),
                            vulnerability.get("advisoryurl", ""),
                        ))

    threshold = SEVERITY_ORDER[args.fail_on]
    failing = [f for f in findings if SEVERITY_ORDER.get(f[4], 4) >= threshold]
    warning = [f for f in findings if SEVERITY_ORDER.get(f[4], 4) < threshold]

    lines = ["## NuGet vulnerabilities (`dotnet list package --vulnerable --include-transitive`)", ""]
    if not findings:
        lines.append("No known vulnerabilities in any direct or transitive package.")
    else:
        lines += ["| Project | TFM | Package | Version | Severity | Advisory |", "|---|---|---|---|---|---|"]
        for project_path, tfm, package_id, version, severity, url in sorted(findings, key=lambda f: -SEVERITY_ORDER.get(f[4], 4)):
            lines.append(f"| `{project_path}` | {tfm} | `{package_id}` | {version} | **{severity}** | {url} |")
    lines.append("")
    text = "\n".join(lines)
    print(text)
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as handle:
            handle.write(text + "\n")

    for project_path, _tfm, package_id, version, severity, url in warning:
        print(f"::warning title=Vulnerable package ({severity})::{package_id} {version} in {project_path} {url}")
    for project_path, _tfm, package_id, version, severity, url in failing:
        print(f"::error title=Vulnerable package ({severity})::{package_id} {version} in {project_path} {url}")

    return 1 if failing else 0


if __name__ == "__main__":
    sys.exit(main())
