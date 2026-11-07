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
printf "[3/4] Checking deploy/.env configuration... "
if [ ! -f "$ENV_FILE" ]; then
  echo "Generating fresh .env..."
  if [ -f "$ENV_EXAMPLE" ]; then
    MASTER_SECRET="$(openssl rand -base64 48 2>/dev/null || head -c 48 /dev/urandom | base64 | tr -d '\n')"
    PG_PASSWORD="$(openssl rand -base64 16 2>/dev/null || head -c 16 /dev/urandom | base64 | tr -d '/+=\n')"

    sed -e "s/^POSTGRES_PASSWORD=.*$/POSTGRES_PASSWORD=$PG_PASSWORD/" \
        -e "s/^DLE_MASTER_SECRET=.*$/DLE_MASTER_SECRET=$MASTER_SECRET/" \
        -e "s/^DLE_DOMAIN=.*$/DLE_DOMAIN=localhost/" \
        -e "s/^DLE_TLS=.*$/DLE_TLS=internal/" \
        "$ENV_EXAMPLE" > "$ENV_FILE"
    echo "      Created $ENV_FILE with generated DLE_MASTER_SECRET and POSTGRES_PASSWORD."
  else
    echo "FAILED ($ENV_EXAMPLE template not found)"
    exit 1
  fi
else
  echo "OK ($ENV_FILE exists)"
fi

# 4. Summary & next steps
echo "[4/4] Environment Ready!"
echo ""
echo "Endpoints for Local Development (using docker-compose.override.yml):"
echo "  * Edge (Data Plane, Redirects, QR): http://localhost:8080"
echo "  * Control API & OpenAPI (Scalar):   http://localhost:8081/scalar/v1"
echo "  * Administration SPA:               http://localhost:8081/admin/"
echo "  * Edge Health Check:                http://localhost:8080/healthz"
echo "  * Control Health Check:             http://localhost:8081/healthz"
echo ""
echo "Automatic Bootstrap:"
echo "  When the database is empty on first start, Dle.Control automatically provisions"
echo "  an initial tenant ('default') and a root Owner API key. Find your key by running:"
echo "    docker compose logs dle-control | grep -A 8 'BOOTSTRAP'"
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
    docker compose up -d --build
  else
    echo "Starting compose stack..."
    docker compose up -d
  fi
  echo "Stack started! Check status with: docker compose ps -a"
else
  echo "To start the local stack now, run:"
  echo "  cd deploy && docker compose up -d --build"
  echo "or with this script:"
  echo "  ./setup-dev.sh --build"
fi
