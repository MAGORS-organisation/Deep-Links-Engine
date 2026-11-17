#!/usr/bin/env bash
# =============================================================================
# Runs the k6 resolve profile (resolve.js) against a compose stack on this machine, with no staging
# instance and no DNS. release.yml runs it as the k6 gate, and loadtest.yml runs it on demand.
#
# Needs a stack started with compose.loadtest.yml (see its header) whose control plane got its
# first owner key from DLE_BOOTSTRAP_INITIAL_API_KEY, plus k6, Node 22, curl and jq. It then:
#   1. points the link host at 127.0.0.1 (/etc/hosts, through passwordless sudo when needed);
#   2. registers the host as a link domain with the owner key. With
#      Dle:Control:RequireDomainOwnershipVerification at its default (false) the domain is active at
#      once, and creating a link needs nothing more than the domain;
#   3. seeds the corpus with seed.js and checks that the edge serves every hot link;
#   4. runs resolve.js straight against the edge. Its thresholds decide the exit status;
#   5. reads the two numbers k6 cannot see (cache hit rate, dropped click events) from the edge's
#      /metrics and reports them, with the overrides the run used.
#
#   DLE_API_KEY=<owner key> bash tests/load/run-on-compose.sh [output-dir]
#
# Optional: DLE_CONTROL_URL (default http://127.0.0.1:8081), DLE_EDGE_PORT (8080),
# DLE_LINK_HOST (link.dle.test), DLE_LOADTEST_TOTAL (10000), DLE_LOADTEST_HOT (100).
# The output directory (default k6-out) receives k6-summary.json, summary.json and report.md.
# =============================================================================
set -euo pipefail

out=${1:-k6-out}
control=${DLE_CONTROL_URL:-http://127.0.0.1:8081}
edge_port=${DLE_EDGE_PORT:-8080}
host=${DLE_LINK_HOST:-link.dle.test}
: "${DLE_API_KEY:?DLE_API_KEY must be the stack owner key (the DLE_BOOTSTRAP_INITIAL_API_KEY it started with)}"
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

die() { echo "run-on-compose: $*" >&2; exit 1; }

for tool in k6 node curl jq; do
  command -v "$tool" >/dev/null || die "$tool is required"
done
mkdir -p "$out"
out=$(cd "$out" && pwd)

# 1. The link host resolves to this machine.
if ! getent hosts "$host" | grep -q '^127\.0\.0\.1\b'; then
  echo "127.0.0.1 $host" | sudo -n tee -a /etc/hosts >/dev/null ||
    die "$host must resolve to 127.0.0.1; add '127.0.0.1 $host' to /etc/hosts"
fi

# 2. The link domain.
api() { curl -sS -H "Authorization: Bearer $DLE_API_KEY" -H 'Content-Type: application/json' "$@"; }
status=$(api -o "$out/domain.json" -w '%{http_code}' -X POST "$control/api/v1/domains" \
  --data "$(jq -cn --arg host "$host" '{host: $host}')")
case $status in
  201) domain=$(jq -c . "$out/domain.json") ;;
  409) # registered by an earlier run against the same stack
       domain=$(api "$control/api/v1/domains" | jq -c --arg host "$host" '.items[] | select(.host == $host)') ;;
  *) die "registering $host returned HTTP $status: $(head -c 400 "$out/domain.json")" ;;
esac
rm -f "$out/domain.json"
domain_id=$(jq -r '.id // empty' <<<"$domain")
[ -n "$domain_id" ] || die "no domain id for $host"
[ "$(jq -r '.is_active' <<<"$domain")" = true ] ||
  die "$host is registered but inactive; this stack requires domain ownership verification"
echo "link domain $host ($domain_id)"

# 3. The corpus. seed.js writes corpus.json next to resolve.js, where resolve.js reads it.
DLE_CONTROL_URL=$control DLE_DOMAIN_ID=$domain_id DLE_LINK_HOST=$host \
  node "$here/seed.js" --total "${DLE_LOADTEST_TOTAL:-10000}" --hot "${DLE_LOADTEST_HOT:-100}"

base="http://$host:$edge_port"
served() { curl -s -o /dev/null -w '%{http_code}' -A 'Mozilla/5.0 (X11; Linux x86_64) Firefox/140.0' "$base/$1" || true; }
mapfile -t hot < <(jq -r '.hot[]' "$here/corpus.json")
code=000
for _ in $(seq 1 60); do
  code=$(served "${hot[0]}")
  case $code in 302 | 200) break ;; esac
  sleep 1
done
for slug in "${hot[@]}"; do
  code=$(served "$slug")
  case $code in 302 | 200) ;; *) die "the edge does not serve $base/$slug (HTTP $code)" ;; esac
done
echo "edge serves all ${#hot[@]} hot links on $base"

# 4. The profile. handleSummary writes summary.json to the working directory, hence the cd.
k6_status=0
(cd "$out" && k6 run -e BASE_URL="$base" --summary-export k6-summary.json "$here/resolve.js") || k6_status=$?

# 5. What k6 cannot see, read once after the run so the scrape is not part of the measurement.
metrics=$(curl -fsS "http://127.0.0.1:$edge_port/metrics" || true)
hit_rate=n/a dropped=n/a
if [ -n "$metrics" ]; then
  # Over lookups of links that exist: dle_cache_hit_ratio also counts the profile's deliberate 5 %
  # of never-existing slugs, each a database lookup, so it cannot reach 95 % by construction. In the
  # resolve histogram cache="l1" means no database lookup (L1 or L2) and cache="miss" means one.
  hit_rate=$(awk '$1 ~ /^dle_resolve_duration(_seconds)?_count[{]/ && $1 ~ /cache="(l1|miss)"/ &&
      $1 !~ /outcome="(not_found|gone|blocked|rate_limited|unavailable)"/ { total += $2; if ($1 ~ /cache="l1"/) hit += $2 }
    END { if (total) printf "%.1f %%", 100 * hit / total; else printf "n/a" }' <<<"$metrics")
  # The counter has no series until the first drop, so its absence from a working scrape means 0.
  dropped=$(awk '$1 ~ /^dle_click_events_dropped_total([{]|$)/ { sum += $2 } END { printf "%d", sum }' <<<"$metrics")
fi

cpu=$(awk -F': ' '/^model name/ { print $2; exit }' /proc/cpuinfo 2>/dev/null || true)
# dropped_iterations are arrivals k6 could not start on time: a generator that fell behind sends
# fewer requests than the profile asks for, which flatters the latency, so the report says how many.
requests=n/a skipped=n/a
if [ -f "$out/k6-summary.json" ]; then
  requests=$(jq -r '.metrics.http_reqs.count // "n/a"' "$out/k6-summary.json")
  skipped=$(jq -r '.metrics.dropped_iterations.count // 0' "$out/k6-summary.json")
fi
{
  if [ "$k6_status" -eq 0 ]; then
    echo "k6 resolve profile **passed** (thresholds in tests/load/resolve.js)."
  else
    echo "k6 resolve profile **failed** (k6 exit $k6_status)."
  fi
  echo
  echo "- Target: compose stack and k6 on one machine, $(nproc) vCPU${cpu:+ ($cpu)}, one edge replica, straight to the edge without the reverse proxy"
  echo "- Edge overrides (tests/load/compose.loadtest.yml): resolve limiter off, 404 budget 100 000/s, reverse-DNS crawler verification off, Production environment, CPU limit ${DLE_LOADTEST_EDGE_CPUS:-2}, /metrics exposed"
  echo "- Requests sent: $requests; arrivals k6 could not start on time (generator, not the edge): $skipped"
  echo "- Cache hit rate over existing links (L1 + L2, target ≥ 95 %): $hit_rate"
  echo "- Dropped click events (target 0): $dropped"
} >"$out/report.md"
cat "$out/report.md"

exit "$k6_status"
