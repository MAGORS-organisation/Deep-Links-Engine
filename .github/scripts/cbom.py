#!/usr/bin/env python3
"""Cryptographic Bill of Materials (CBOM) generator for the Deep Link Engine.

docs/zadanie.md section E.5.3 Phase 0 makes a CBOM a mandatory release artefact, and section E.4.1
is the inventory it has to reflect (K1..K9). This script emits a CycloneDX 1.6 JSON document whose
components are cryptographic-asset entries:

  * one component per algorithm the product uses (HMAC-SHA-256, Ed25519, ML-DSA-65, ...),
  * one component per section E.4.1 inventory line (K1..K9) as related-crypto-material / protocol,
  * dependencies linking each inventory line to the algorithms it relies on.

Evidence is real, not asserted: every algorithm component carries evidence.occurrences found by
scanning src/Dle.Crypto (and the hosts, the deploy tree and the workflows) for the identifiers that
name it. An algorithm the inventory lists but the scan cannot find is still emitted (the inventory is
normative) and flagged with the property dle:evidence = inventory-only so a reviewer sees the gap.

The output is deliberately independent of any third-party tool: the CycloneDX .NET tooling does not
enumerate cryptography, and a CBOM that could only be produced on one vendor's machine would not be
an inventory anybody could audit.

Usage:
  cbom.py --repo-root . --version 0.1.0 --output cbom.cdx.json
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

SPEC_VERSION = "1.6"
PRODUCT_REF = "pkg:github/MAGORS-organisation/Deep-Links-Engine@{version}"

# --------------------------------------------------------------------------------------------------
# Algorithms. "patterns" are the identifiers that prove use in source; they are searched in the
# scanned directories. OIDs are given where a registration exists.
# --------------------------------------------------------------------------------------------------
ALGORITHMS: list[dict] = [
    {
        "ref": "alg:hmac-sha-256",
        "name": "HMAC-SHA-256",
        "oid": "1.2.840.113549.2.9",
        "primitive": "mac",
        "parameterSetIdentifier": "256",
        "cryptoFunctions": ["tag", "verify"],
        "classicalSecurityLevel": 256,
        "nistQuantumSecurityLevel": 5,
        "patterns": [r"HMACSHA256", r"HMAC-SHA-256", r"\bHS256\b", r"HmacSigner"],
        "note": "Click token signature (K2), webhook v1 slot (K4), IP hash salt (K9). "
                "Symmetric: no post-quantum exposure.",
    },
    {
        "ref": "alg:sha-256",
        "name": "SHA-256",
        "oid": "2.16.840.1.101.3.4.2.1",
        "primitive": "hash",
        "parameterSetIdentifier": "256",
        "cryptoFunctions": ["digest"],
        "classicalSecurityLevel": 128,
        "nistQuantumSecurityLevel": 2,
        "patterns": [r"\bSHA256\b", r"SHA-256", r"HashAlgorithmName\.SHA256"],
        "note": "Underlying digest of HMAC, HKDF and the Feistel round function.",
    },
    {
        "ref": "alg:hkdf-sha-256",
        "name": "HKDF-SHA-256",
        "oid": "1.2.840.113549.1.9.16.3.28",
        "primitive": "kdf",
        "parameterSetIdentifier": "SHA-256",
        "cryptoFunctions": ["keyderive"],
        "classicalSecurityLevel": 256,
        "nistQuantumSecurityLevel": 5,
        "patterns": [r"\bHKDF\b", r"HKDF-SHA-256", r"CryptoKeyDerivation"],
        "note": "Derives every purpose-bound key from Dle:Crypto:MasterSecret (SigningKeyPurposes).",
    },
    {
        "ref": "alg:ed25519",
        "name": "Ed25519",
        "oid": "1.3.101.112",
        "primitive": "signature",
        "parameterSetIdentifier": "Ed25519",
        "curve": "curve25519",
        "cryptoFunctions": ["keygen", "sign", "verify"],
        "classicalSecurityLevel": 128,
        "nistQuantumSecurityLevel": 0,
        "patterns": [r"Ed25519Signer", r"\bEd25519\b"],
        "note": "Control-plane default token algorithm and webhook v2 slot (K4). Broken by Shor; "
                "the v3 migration slot is reserved (section E.5.3).",
    },
    {
        "ref": "alg:ml-dsa-65",
        "name": "ML-DSA-65",
        "oid": "2.16.840.1.101.3.4.3.18",
        "primitive": "signature",
        "parameterSetIdentifier": "ML-DSA-65",
        "cryptoFunctions": ["keygen", "sign", "verify"],
        "classicalSecurityLevel": 192,
        "nistQuantumSecurityLevel": 3,
        "patterns": [r"MlDsa65Signer", r"ML-DSA-65", r"MLDSA65", r"MLDsaParameters"],
        "note": "FIPS 204. Selectable through Dle:Crypto:SigningAlgorithm; BouncyCastle implementation "
                "(section E.5.2 fallback).",
    },
    {
        "ref": "alg:slh-dsa-sha2-128s",
        "name": "SLH-DSA-SHA2-128s",
        "oid": "2.16.840.1.101.3.4.3.20",
        "primitive": "signature",
        "parameterSetIdentifier": "SLH-DSA-SHA2-128s",
        "cryptoFunctions": ["keygen", "sign", "verify"],
        "classicalSecurityLevel": 128,
        "nistQuantumSecurityLevel": 1,
        "patterns": [r"SlhDsa128sSigner", r"SLHDSA128s", r"SLH-DSA"],
        "note": "FIPS 205, stateless hash-based. Offered as a conservative alternative to ML-DSA.",
    },
    {
        "ref": "alg:ed25519-mldsa65-composite",
        "name": "Ed25519+ML-DSA-65 (composite)",
        "primitive": "combiner",
        "parameterSetIdentifier": "Ed25519+MLDSA65",
        "cryptoFunctions": ["sign", "verify"],
        "classicalSecurityLevel": 128,
        "nistQuantumSecurityLevel": 3,
        "patterns": [r"CompositeSigner", r"Ed25519MlDsa65", r"Ed25519\+MLDSA65"],
        "note": "Hybrid signature per draft-ietf-lamps-pq-composite-sigs; the section E.5.3 Phase 2 default.",
    },
    {
        "ref": "alg:argon2id",
        "name": "Argon2id",
        "primitive": "kdf",
        "parameterSetIdentifier": "Argon2id (RFC 9106)",
        "cryptoFunctions": ["keyderive", "digest"],
        "classicalSecurityLevel": 256,
        "nistQuantumSecurityLevel": 5,
        "patterns": [r"Argon2id", r"Argon2PasswordHasher", r"ApiKeyHasher", r"Konscious\.Security\.Cryptography"],
        "note": "API key at-rest hash (K5).",
    },
    {
        "ref": "alg:keyed-feistel-permutation",
        "name": "Keyed Feistel permutation (HMAC-SHA-256 round function)",
        "primitive": "other",
        "parameterSetIdentifier": "4 rounds (Dle:Crypto:SlugFeistelRounds)",
        "cryptoFunctions": ["encrypt", "decrypt"],
        "classicalSecurityLevel": 0,
        "nistQuantumSecurityLevel": 0,
        "patterns": [r"FeistelPermutation", r"SlugFeistelRounds"],
        "note": "Slug generation (ADR-007): hides the sequence behind an unpredictable order. Not a "
                "confidentiality primitive; the security level is deliberately reported as 0.",
    },
    {
        "ref": "alg:csprng",
        "name": "CSPRNG (System.Security.Cryptography.RandomNumberGenerator)",
        "primitive": "drbg",
        "cryptoFunctions": ["generate"],
        "classicalSecurityLevel": 256,
        "nistQuantumSecurityLevel": 5,
        "patterns": [r"RandomNumberGenerator", r"ClaimCodeGenerator"],
        "note": "API keys (K5, 256 bits), claim codes (K3, 30 bits + TTL + rate limit), key generation.",
    },
    {
        "ref": "alg:aes-256-gcm",
        "name": "AES-256-GCM",
        "oid": "2.16.840.1.101.3.4.1.46",
        "primitive": "ae",
        "parameterSetIdentifier": "256",
        "mode": "gcm",
        "cryptoFunctions": ["encrypt", "decrypt"],
        "classicalSecurityLevel": 256,
        "nistQuantumSecurityLevel": 5,
        "patterns": [r"AesGcm", r"AES-256-GCM"],
        "note": "TLS record protection (K1) and encryption at rest (K8). Provided by the OS TLS stack "
                "and the database/disk layer, not by DLE code; expected to be inventory-only.",
    },
    {
        "ref": "alg:x25519mlkem768",
        "name": "X25519MLKEM768 (hybrid key agreement)",
        "primitive": "kem",
        "parameterSetIdentifier": "X25519MLKEM768",
        "cryptoFunctions": ["encapsulate", "decapsulate"],
        "classicalSecurityLevel": 128,
        "nistQuantumSecurityLevel": 3,
        "patterns": [r"X25519MLKEM768", r"MLKem", r"HybridPqEnabled"],
        "note": "TLS 1.3 hybrid group (K1). Negotiated by the OS or the reverse proxy (section E.5.3 "
                "Phase 0); Dle:Crypto:HybridPqEnabled records the deployment's intent.",
    },
    {
        "ref": "alg:ecdsa-p256-sha256",
        "name": "ECDSA P-256 with SHA-256",
        "oid": "1.2.840.10045.4.3.2",
        "primitive": "signature",
        "parameterSetIdentifier": "P-256",
        "curve": "secp256r1",
        "cryptoFunctions": ["sign", "verify"],
        "classicalSecurityLevel": 128,
        "nistQuantumSecurityLevel": 0,
        "patterns": [r"cosign", r"sigstore"],
        "note": "Release artefact signing via Sigstore/cosign keyless (K7). Highest-priority "
                "post-quantum migration item (section E.4.1, CNSA 2.0 deadline 2030).",
    },
]

# --------------------------------------------------------------------------------------------------
# Section E.4.1 inventory. Each line becomes a component of its own that depends on the algorithms
# it uses.
# --------------------------------------------------------------------------------------------------
INVENTORY: list[dict] = [
    {"id": "K1", "ref": "inv:k1-tls-edge", "name": "TLS on the edge", "assetType": "protocol",
     "lifetime": "seconds", "pqRisk": "medium (harvest now, decrypt later; low-value content)",
     "uses": ["alg:x25519mlkem768", "alg:aes-256-gcm"],
     "protocol": {"type": "tls", "version": "1.3",
                  "cipherSuites": [{"name": "TLS_AES_256_GCM_SHA384",
                                    "algorithms": ["alg:aes-256-gcm", "alg:x25519mlkem768"],
                                    "identifiers": ["0x13", "0x02"]}]}},
    {"id": "K2", "ref": "inv:k2-click-token", "name": "Click token (click_id signature)",
     "assetType": "related-crypto-material", "material": "token", "lifetime": "<= 90 days",
     "pqRisk": "none (symmetric 256-bit; 128-bit after Grover)",
     "uses": ["alg:hmac-sha-256", "alg:hkdf-sha-256"]},
    {"id": "K3", "ref": "inv:k3-claim-code", "name": "Claim code",
     "assetType": "related-crypto-material", "material": "token",
     "lifetime": "1 hour (Dle:Attribution:ClaimCode:TtlMinutes)", "pqRisk": "none",
     "uses": ["alg:csprng"]},
    {"id": "K4", "ref": "inv:k4-webhook-signature", "name": "Webhook signature (v1 HMAC + v2 Ed25519)",
     "assetType": "related-crypto-material", "material": "signature", "lifetime": "minutes",
     "pqRisk": "yes for Ed25519 (Shor); v3 slot reserved",
     "uses": ["alg:hmac-sha-256", "alg:ed25519", "alg:ml-dsa-65", "alg:ed25519-mldsa65-composite"]},
    {"id": "K5", "ref": "inv:k5-api-keys", "name": "API keys (256-bit random, Argon2id at rest)",
     "assetType": "related-crypto-material", "material": "credential", "lifetime": "years",
     "pqRisk": "none", "uses": ["alg:csprng", "alg:argon2id"]},
    {"id": "K6", "ref": "inv:k6-sdk-keys", "name": "SDK key (public identifier, domain/bundle bound)",
     "assetType": "related-crypto-material", "material": "credential", "lifetime": "years",
     "pqRisk": "n/a (not a secret)", "uses": ["alg:csprng"]},
    {"id": "K7", "ref": "inv:k7-release-signing", "name": "Release artefact signing (Sigstore/cosign keyless)",
     "assetType": "related-crypto-material", "material": "signature", "lifetime": "years",
     "pqRisk": "yes - highest migration priority", "uses": ["alg:ecdsa-p256-sha256"]},
    {"id": "K8", "ref": "inv:k8-encryption-at-rest", "name": "Encryption at rest (database / disk)",
     "assetType": "related-crypto-material", "material": "key", "lifetime": "years",
     "pqRisk": "none", "uses": ["alg:aes-256-gcm"]},
    {"id": "K9", "ref": "inv:k9-ip-hash-salt", "name": "IP hash salt (daily rotation)",
     "assetType": "related-crypto-material", "material": "salt",
     "lifetime": "24 h (Dle:Privacy:IpSaltRotationHours)", "pqRisk": "none",
     "uses": ["alg:hmac-sha-256", "alg:hkdf-sha-256"]},
    {"id": "K-slug", "ref": "inv:slug-permutation", "name": "Slug permutation key (ADR-007)",
     "assetType": "related-crypto-material", "material": "secret-key",
     "lifetime": "lifetime of the deployment", "pqRisk": "none (not a confidentiality boundary)",
     "uses": ["alg:keyed-feistel-permutation", "alg:hkdf-sha-256"]},
]

SCAN_DIRS = ("src/Dle.Crypto", "src/Dle.Domain", "src/Dle.Edge", "src/Dle.Control",
             ".github/workflows", "deploy")
SCAN_SUFFIXES = (".cs", ".yml", ".yaml", ".json", ".md")
SKIP_PARTS = {"bin", "obj", "node_modules", "wwwroot"}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--repo-root", default=".")
    parser.add_argument("--version", default="0.0.0")
    parser.add_argument("--output", required=True)
    parser.add_argument("--max-occurrences", type=int, default=12)
    return parser.parse_args()


def scan(root: Path) -> list[tuple[str, int, str]]:
    """Every (relative path, line number, text) in the scanned directories."""
    lines: list[tuple[str, int, str]] = []
    for directory in SCAN_DIRS:
        base = root / directory
        if not base.is_dir():
            continue
        for path in sorted(base.rglob("*")):
            if not path.is_file() or path.suffix not in SCAN_SUFFIXES:
                continue
            if SKIP_PARTS.intersection(path.parts):
                continue
            try:
                text = path.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            rel = path.relative_to(root).as_posix()
            for number, line in enumerate(text.splitlines(), start=1):
                lines.append((rel, number, line))
    return lines


def occurrences(lines: list[tuple[str, int, str]], patterns: list[str], limit: int) -> list[dict]:
    regex = re.compile("|".join(patterns))
    found: list[dict] = []
    for rel, number, line in lines:
        if regex.search(line):
            found.append({"location": rel, "line": number})
            if len(found) >= limit:
                break
    return found


def algorithm_component(entry: dict, lines: list[tuple[str, int, str]], limit: int) -> dict:
    props = {key: entry[key] for key in ("primitive", "parameterSetIdentifier", "curve", "mode",
                                          "cryptoFunctions", "classicalSecurityLevel",
                                          "nistQuantumSecurityLevel") if key in entry}
    props["executionEnvironment"] = "software-plain-ram"
    props["implementationPlatform"] = "generic"
    found = occurrences(lines, entry["patterns"], limit)
    component = {
        "type": "cryptographic-asset",
        "bom-ref": entry["ref"],
        "name": entry["name"],
        "description": entry["note"],
        "cryptoProperties": {"assetType": "algorithm", "algorithmProperties": props},
        "properties": [
            {"name": "dle:evidence", "value": "source-scan" if found else "inventory-only"},
        ],
    }
    if "oid" in entry:
        component["cryptoProperties"]["oid"] = entry["oid"]
    if found:
        component["evidence"] = {"occurrences": found}
    return component


def inventory_component(entry: dict) -> dict:
    component = {
        "type": "cryptographic-asset",
        "bom-ref": entry["ref"],
        "name": f"{entry['id']} - {entry['name']}",
        "description": f"docs/zadanie.md section E.4.1 line {entry['id']}",
        "properties": [
            {"name": "dle:inventory-id", "value": entry["id"]},
            {"name": "dle:artefact-lifetime", "value": entry["lifetime"]},
            {"name": "dle:pq-risk", "value": entry["pqRisk"]},
        ],
    }
    if entry["assetType"] == "protocol":
        component["cryptoProperties"] = {"assetType": "protocol",
                                         "protocolProperties": entry["protocol"]}
    else:
        component["cryptoProperties"] = {
            "assetType": "related-crypto-material",
            "relatedCryptoMaterialProperties": {"type": entry["material"],
                                                "algorithmRef": entry["uses"][0]},
        }
    return component


def main() -> int:
    args = parse_args()
    root = Path(args.repo_root).resolve()
    lines = scan(root)

    known_refs = {a["ref"] for a in ALGORITHMS}
    for entry in INVENTORY:
        unknown = [u for u in entry["uses"] if u not in known_refs]
        if unknown:
            print(f"error: {entry['id']} references unknown algorithm(s) {unknown}", file=sys.stderr)
            return 2

    algorithms = [algorithm_component(a, lines, args.max_occurrences) for a in ALGORITHMS]
    inventory = [inventory_component(i) for i in INVENTORY]
    product_ref = PRODUCT_REF.format(version=args.version)
    timestamp = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")

    bom = {
        "$schema": "http://cyclonedx.org/schema/bom-1.6.schema.json",
        "bomFormat": "CycloneDX",
        "specVersion": SPEC_VERSION,
        "serialNumber": f"urn:uuid:{uuid.uuid4()}",
        "version": 1,
        "metadata": {
            "timestamp": timestamp,
            "tools": {"components": [{"type": "application", "name": "dle-cbom", "version": "1",
                                      "description": ".github/scripts/cbom.py (this repository)"}]},
            "component": {
                "type": "application",
                "bom-ref": product_ref,
                "name": "Deep Link Engine",
                "version": args.version,
                "licenses": [{"license": {"id": "MIT"}}],
            },
            "properties": [
                {"name": "dle:cbom-source",
                 "value": "docs/zadanie.md section E.4.1 + source scan of " + ", ".join(SCAN_DIRS)},
                {"name": "dle:scanned-lines", "value": str(len(lines))},
            ],
        },
        "components": algorithms + inventory,
        "dependencies": [
            {"ref": product_ref, "dependsOn": [i["ref"] for i in INVENTORY]},
            *({"ref": i["ref"], "dependsOn": i["uses"]} for i in INVENTORY),
        ],
    }

    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(bom, indent=2) + "\n", encoding="utf-8")

    inventory_only = [a["name"] for a in algorithms if a["properties"][0]["value"] == "inventory-only"]
    print(f"CBOM written to {output}: {len(algorithms)} algorithms, {len(inventory)} inventory lines, "
          f"{len(lines)} source lines scanned.")
    if inventory_only:
        print("inventory-only (no source evidence found): " + ", ".join(inventory_only))
    return 0


if __name__ == "__main__":
    sys.exit(main())
