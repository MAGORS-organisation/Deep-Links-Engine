# Coverage-Guided Fuzzing with SharpFuzz

Deep Link Engine (DLE) implements coverage-guided fuzz testing as specified in [docs/zadanie.md §D.4](../zadanie.md) and §E.2.2.

This document describes the fuzzing architecture, how to instrument assemblies, and how to execute fuzzing campaigns against the edge and domain components.

---

## 1. Architecture Overview

Fuzz testing in DLE is split into two complementary layers:

```
┌────────────────────────────────────────────────────────────────────────┐
│                        Deep Link Engine Fuzzing                        │
├──────────────────────────────────┬─────────────────────────────────────┤
│   Deterministic Property Tests   │    Coverage-Guided Fuzz Driver      │
│     (Dle.SecurityTests)          │        (Dle.FuzzDriver)             │
│                                  │                                     │
│  - Runs on every build and CI    │  - SharpFuzz + libFuzzer driver     │
│  - Powered by CsCheck            │  - Instruments IL branches          │
│  - Structured seed mutations     │  - Guided by code coverage feedback │
│  - Reproducible with seed string │  - Runs in nightly & security audits│
└──────────────────────────────────┴─────────────────────────────────────┘
```

### 1.1 Fuzz Targets (`FuzzTargets.cs`)

Located in [`tests/Dle.SecurityTests/Fuzzing/FuzzTargets.cs`](../tests/Dle.SecurityTests/Fuzzing/FuzzTargets.cs), the four canonical fuzz targets exercise parser hot paths:

| Target Name | Component Tested | Security / Correctness Invariants |
|---|---|---|
| `url` | `TargetUrlPolicy`, `HostNormalizer`, `SafeUrl`, `SlugPolicy` | Prevents SSRF (`TC-162`), open redirects (`TC-164`), scheme smuggling (`javascript:`, `data:`). Verifies syntax normalization. |
| `user-agent` | `ClientClassifier`, `CrawlerCatalog`, `WebViewCatalog` | Guards against ReDoS catastrophic backtracking (T-10) in user-agent classification regexes and prevents crash on malformed header strings. |
| `routing-rules` | `RoutingRuleParser`, System.Text.Json deserializer, schema validation | Guards against JSON deserialization attacks (T-12), recursion/depth exhaustion, unhandled exceptions on invalid rule constructs. |
| `install-referrer` | Google Play Store install referrer parameter parsing | Validates parameter unpacking and integrity, guards against parsing corruption on tampered `dl_cid` tokens. |

### 1.2 SharpFuzz Driver (`Dle.FuzzDriver`)

[`tests/Dle.FuzzDriver`](../tests/Dle.FuzzDriver/Dle.FuzzDriver.csproj) is a dedicated console application (`OutputType=Exe`) referencing `SharpFuzz` and `Dle.SecurityTests`. It exposes a CLI entry point:

```bash
dotnet run --project tests/Dle.FuzzDriver --no-build -- [target]
```

Where `[target]` is one of `url`, `user-agent`, `routing-rules`, or `install-referrer`.

---

## 2. Running SharpFuzz Locally (Linux / WSL / Docker)

SharpFuzz relies on binary IL instrumentation and libFuzzer (native Clang runtime).

### Step 1: Install prerequisites

```bash
# 1. Install SharpFuzz global CLI tool
dotnet tool install --global SharpFuzz.CommandLine

# 2. Ensure clang / libfuzzer is installed (Ubuntu/Debian)
sudo apt-get update && sudo apt-get install -y clang libfuzzer-18-dev
```

### Step 2: Build the solution in Release mode

```bash
dotnet build Dle.sln -c Release
```

### Step 3: Instrument target assemblies

Before running the driver, use `sharpfuzz` to rewrite the compiled assemblies with coverage tracking hooks:

```bash
# Instrument assemblies under test
sharpfuzz tests/Dle.FuzzDriver/bin/Release/net10.0/Dle.Domain.dll
sharpfuzz tests/Dle.FuzzDriver/bin/Release/net10.0/Dle.Edge.dll
sharpfuzz tests/Dle.FuzzDriver/bin/Release/net10.0/Dle.SecurityTests.dll
```

### Step 4: Run fuzzing with libFuzzer

Create an initial corpus directory (optionally seeding it with test samples) and launch:

```bash
mkdir -p corpus/routing-rules
echo '[]' > corpus/routing-rules/empty.json
echo '[{"id":"default","then":{"action":"web","url":"https://example.com"}}]' > corpus/routing-rules/simple.json

# Execute SharpFuzz driver with libFuzzer
dotnet tests/Dle.FuzzDriver/bin/Release/net10.0/Dle.FuzzDriver.dll routing-rules corpus/routing-rules
```

For continuous fuzzing campaigns, specify a timeout and memory limit:

```bash
dotnet tests/Dle.FuzzDriver/bin/Release/net10.0/Dle.FuzzDriver.dll url corpus/url \
  -max_len=4096 \
  -timeout=5 \
  -rss_limit_mb=2048
```

---

## 3. Deterministic Property-Based Execution (`FuzzHarnessTests`)

To ensure fuzz targets are continuously tested on all developer machines and in standard PR builds without requiring native Clang/libFuzzer setup, `Dle.SecurityTests` includes [`FuzzHarnessTests`](../tests/Dle.SecurityTests/Fuzzing/FuzzHarnessTests.cs):

```bash
dotnet run --project tests/Dle.SecurityTests --no-build -- -class "Dle.SecurityTests.Fuzzing.FuzzHarnessTests"
```

### Reproducing Failures

When a test failure occurs under `FuzzHarnessTests`, CsCheck prints the exact reproduction seed:

```
CsCheck.CsCheckException : Set seed: "6F31UT3raRIm" or -e CsCheck_Seed=6F31UT3raRIm to reproduce
```

To deterministically re-run the exact failing input:

```bash
# PowerShell
$env:CsCheck_Seed = "6F31UT3raRIm"
dotnet run --project tests/Dle.SecurityTests --no-build -- -class "Dle.SecurityTests.Fuzzing.FuzzHarnessTests"

# Bash
CsCheck_Seed="6F31UT3raRIm" dotnet run --project tests/Dle.SecurityTests --no-build -- -class "Dle.SecurityTests.Fuzzing.FuzzHarnessTests"
```

---

## 4. Nightly CI Integration

The nightly workflow [`.github/workflows/nightly.yml`](../../.github/workflows/nightly.yml) executes `FuzzHarnessTests` with an extended time budget (`CsCheck_Time=45` seconds per property). In dedicated security fuzzing environments, `Dle.FuzzDriver` is used with a persistent corpus store to discover edge-case parser vulnerabilities.
