#!/bin/sh
set -eu
# One real conversation, end to end: the real AgentRuntime and model against the LOCAL Hospital API
# (synthetic clinic). It registers a synthetic patient and books an appointment there on every run;
# Hospital keeps the patient; the appointment is cancelled at the end. WhatsApp is simulated: no message leaves this machine.
# Usage: scripts/live-agent.sh [test name fragment]   e.g. scripts/live-agent.sh TappedSlot  (needs no AI provider)
grep -q '^NVIDIA_API_KEY=.' .env || { echo "NVIDIA_API_KEY missing in .env" >&2; exit 1; }
test_database="recepcion_live_$(date +%s)_$$"
docker compose exec -T db createdb -U recepcion "$test_database"
trap 'docker compose exec -T db dropdb -U recepcion "$test_database"' EXIT
docker run --rm --network recepcion_default --network hospital --env-file .env -e TEST_DB_NAME="$test_database" -e LIVE_FILTER="${1:-}" -v "$PWD:/src" -v recepcion-nuget:/root/.nuget/packages -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'export TEST_DATABASE="Host=db;Database=${TEST_DB_NAME};Username=recepcion;Password=${POSTGRES_PASSWORD}"; dotnet test backend.Tests/Recepcion.Tests.csproj --filter "Category=Live${LIVE_FILTER:+&DisplayName~$LIVE_FILTER}" --logger "console;verbosity=detailed"'
