#!/usr/bin/env bash
# =============================================================================
# Deep Link Engine (DLE) - Local Developer Setup Script (POSIX / Bash)
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ENV_FILE="$SCRIPT_DIR/.env"
ENV_EXAMPLE="$SCRIPT_DIR/.env.example"

CHECK_ONLY=false
BUILD_STACK=false
UP_STACK=false
ASPIRE_MODE=false

for arg in "$@"; do
  case "$arg" in
    --check) CHECK_ONLY=true ;;
    --build) BUILD_STACK=true ;;
    --up) UP_STACK=true ;;
    --aspire) ASPIRE_MODE=true ;;
    --help|-h)
      echo "Usage: $0 [--check] [--build] [--up] [--aspire]"
      echo "  --check   Validate prerequisites and configuration only"
      echo "  --build   Build from source and start docker compose stack"
      echo "  --up      Start docker compose stack without rebuilding"
      echo "  --aspire  Launch local services via .NET Aspire AppHost"
      exit 0
      ;;
  esac
done

echo "================================================================================"
echo " Deep Link Engine (DLE) - Local Developer Setup"
echo "================================================================================"

# 1. Check .NET SDK
printf "[1/4] Checking .NET SDK... "
if command -v dotnet >/dev/null 2>&1; then
  DOTNET_VER="$(dotnet --version)"
  echo "OK (.NET $DOTNET_VER)"
else
  echo "WARNING (dotnet not found in PATH; required for local builds without Docker)"
fi

# 2. Check Docker
printf "[2/4] Checking Docker & Docker Compose... "
HAS_DOCKER=false
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
  DOCKER_VER="$(docker --version | head -1)"
  COMPOSE_VER="$(docker compose version | head -1)"
  echo "OK ($DOCKER_VER, $COMPOSE_VER)"
  HAS_DOCKER=true
else
  echo "NOT DETECTED (docker or docker compose not in PATH)"
fi

# 3. Check / create .env
# Hex, not base64: base64 contains '/' and '+', which would break the sed expressions below.
random_hex() { openssl rand -hex "$1" 2>/dev/null || od -An -tx1 -N"$1" /dev/urandom | tr -d ' \n'; }
# A-Z a-z 0-9 only. pipefail is off inside the substitution because head closing the pipe ends tr.
random_base62() { (set +o pipefail; LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c "$1"); }
new_owner_key() { printf 'dle_%s_%s' "$(random_base62 8)" "$(random_base62 43)"; }

printf "[3/4] Checking deploy/.env configuration... "
if [ ! -f "$ENV_FILE" ]; then
  echo "Generating fresh .env..."
  if [ -f "$ENV_EXAMPLE" ]; then
    MASTER_SECRET="$(random_hex 32)"
    EDGE_SECRET="$(random_hex 32)"
    PG_PASSWORD="$(random_hex 18)"
    VK_PASSWORD="$(random_hex 18)"
    OWNER_KEY="$(new_owner_key)"

    sed -e "s/^POSTGRES_PASSWORD=.*$/POSTGRES_PASSWORD=$PG_PASSWORD/" \
        -e "s/^VALKEY_PASSWORD=.*$/VALKEY_PASSWORD=$VK_PASSWORD/" \
        -e "s/^DLE_MASTER_SECRET=.*$/DLE_MASTER_SECRET=$MASTER_SECRET/" \
        -e "s/^DLE_EDGE_SECRET=.*$/DLE_EDGE_SECRET=$EDGE_SECRET/" \
        -e "s/^DLE_BOOTSTRAP_INITIAL_API_KEY=.*$/DLE_BOOTSTRAP_INITIAL_API_KEY=$OWNER_KEY/" \
        -e "s/^DLE_DOMAIN=.*$/DLE_DOMAIN=localhost/" \
        -e "s/^DLE_TLS=.*$/DLE_TLS=internal/" \
        "$ENV_EXAMPLE" > "$ENV_FILE"
    chmod 600 "$ENV_FILE"
    echo "      Created $ENV_FILE with generated POSTGRES_PASSWORD, VALKEY_PASSWORD, DLE_MASTER_SECRET,"
    echo "      DLE_EDGE_SECRET and DLE_BOOTSTRAP_INITIAL_API_KEY."
  else
    echo "FAILED ($ENV_EXAMPLE template not found)"
    exit 1
  fi
elif ! grep -Eq '^DLE_BOOTSTRAP_INITIAL_API_KEY=.+' "$ENV_FILE"; then
  # A .env from before the first owner key was configurable: add one, or a fresh database has no way in.
  OWNER_KEY="$(new_owner_key)"
  if grep -q '^DLE_BOOTSTRAP_INITIAL_API_KEY=' "$ENV_FILE"; then
    sed -i.bak "s/^DLE_BOOTSTRAP_INITIAL_API_KEY=.*$/DLE_BOOTSTRAP_INITIAL_API_KEY=$OWNER_KEY/" "$ENV_FILE" && rm -f "$ENV_FILE.bak"
  else
    printf '\nDLE_BOOTSTRAP_INITIAL_API_KEY=%s\n' "$OWNER_KEY" >> "$ENV_FILE"
  fi
  echo "OK ($ENV_FILE exists; added DLE_BOOTSTRAP_INITIAL_API_KEY)"
else
  echo "OK ($ENV_FILE exists)"
fi

# 4. Summary & next steps
echo "[4/4] Environment Ready!"
echo ""
echo "Endpoints for Local Development (docker-compose.yml + docker-compose.dev.yml):"
echo "  * Edge (Data Plane, Redirects, QR): http://localhost:8080"
echo "  * Control API & OpenAPI (Scalar):   http://localhost:8081/scalar/v1"
echo "  * Administration SPA:               http://localhost:8081/admin/"
echo "  * Edge Health Check:                http://localhost:8080/healthz"
echo "  * Control Health Check:             http://localhost:8081/healthz"
echo ""
echo "First sign-in:"
echo "  On an empty database dle-control creates the tenant 'default' with the owner key stored in"
echo "  deploy/.env as DLE_BOOTSTRAP_INITIAL_API_KEY. Sign in at http://localhost:8081/admin/ with it,"
echo "  create a named owner key on the API keys page and revoke 'root-bootstrap'."
echo ""

if [ "$CHECK_ONLY" = true ]; then
  echo "Check completed successfully. Exiting (--check)."
  exit 0
fi

if [ "$ASPIRE_MODE" = true ]; then
  echo "Starting local topology via .NET Aspire AppHost..."
  cd "$ROOT_DIR/deploy/aspire/Dle.AppHost"
  exec dotnet run
fi

if [ "$BUILD_STACK" = true ] || [ "$UP_STACK" = true ]; then
  if [ "$HAS_DOCKER" = false ]; then
    echo "ERROR: Docker is not available in PATH."
    exit 1
  fi
  cd "$SCRIPT_DIR"
  if [ "$BUILD_STACK" = true ]; then
    echo "Building images and launching compose stack..."
    docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build
  else
    echo "Starting compose stack..."
    docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d
  fi
  echo "Stack started! Check status with: cd deploy && docker compose -f docker-compose.yml -f docker-compose.dev.yml ps -a"
else
  echo "To start the local stack now, run:"
  echo "  cd deploy && docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build"
  echo "or with this script:"
  echo "  ./setup-dev.sh --build"
fi
