#!/bin/sh
set -eu
# Runs the agent evals (docs/reception-agent.md, criterio 8): the real AgentRuntime against the
# configured model, with synthetic patients and simulated Hospital/WhatsApp. Consumes model quota.
# Usage: scripts/eval-agent.sh [model]   e.g. scripts/eval-agent.sh nvidia/nemotron-3-ultra-550b-a55b
grep -q '^NVIDIA_API_KEY=.' .env || { echo "NVIDIA_API_KEY missing in .env: the evals cannot run, and a skipped eval is not a pass." >&2; exit 1; }
test_database="recepcion_evals_$(date +%s)_$$"
docker compose exec -T db createdb -U recepcion "$test_database"
trap 'docker compose exec -T db dropdb -U recepcion "$test_database"' EXIT
docker run --rm --network recepcion_default --env-file .env -e TEST_DB_NAME="$test_database" -e AGENT_EVAL=1 -e AGENT_EVAL_MODEL="${1:-}" -v "$PWD:/src" -v recepcion-nuget:/root/.nuget/packages -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'export TEST_DATABASE="Host=db;Database=${TEST_DB_NAME};Username=recepcion;Password=${POSTGRES_PASSWORD}"; dotnet test backend.Tests/Recepcion.Tests.csproj --filter "Category=Eval" --logger "console;verbosity=detailed"'
