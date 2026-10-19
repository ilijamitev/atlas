#!/usr/bin/env bash
# Starts Atlas: the compose stack (SQL Server, Seq, Azurite), then the three services.
# Ctrl+C stops the services. The containers keep running; stop them with
#   docker compose -f platform/docker-compose.yml down
# Honours SQL_PORT, SEQ_PORT and BLOB_PORT, like the compose file.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "Starting SQL Server, Seq and Azurite..."
docker compose -f "$root/platform/docker-compose.yml" up -d --wait

# Build once: three `dotnet run` builds in parallel would fight over Atlas.Shared.
echo "Building..."
dotnet build "$root/src/Atlas.slnx" -nologo -v:q

# On exit, stop everything this script started (dotnet run and the services under it).
trap 'trap - EXIT INT TERM; kill 0 2>/dev/null || true' EXIT INT TERM

for service in Atlas.Simulators Atlas.Screening Atlas.Onboarding; do
  dotnet run --no-build --launch-profile http --project "$root/src/$service" &
done

for url in http://localhost:5300 http://localhost:5200 http://localhost:5100; do
  for _ in $(seq 1 120); do
    if curl -sf "$url/health/ready" >/dev/null; then break; fi
    sleep 1
  done
  curl -sf "$url/health/ready" >/dev/null || { echo "$url didn't become ready." >&2; exit 1; }
done

echo
echo "Atlas is running. Ctrl+C stops the services."
echo "  Onboarding  http://localhost:5100/swagger"
echo "  Screening   http://localhost:5200/swagger"
echo "  Simulators  http://localhost:5300/swagger"
echo "  Seq         http://localhost:${SEQ_PORT:-5341}"
wait
