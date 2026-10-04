#!/bin/sh
set -eu
# Runs the agent evals (docs/reception-agent.md, criterio 8): the real AgentRuntime against the
# configured model, with synthetic patients and simulated Hospital/WhatsApp. Consumes model quota:
# the live agent uses the same key, so never run this while patients are being attended.
# Usage: scripts/eval-agent.sh [model]   e.g. scripts/eval-agent.sh nvidia/nemotron-3-ultra-550b-a55b
#        AGENT_EVAL_FILTER=citas scripts/eval-agent.sh        (one area or one case; a full pass takes about half an hour)
#        AGENT_EVAL_PROVIDER=openai AGENT_EVAL_REASONING=none AGENT_EVAL_SPACING=0.3 scripts/eval-agent.sh gpt-6-luna
#          (grades an OpenAI model with OPENAI_API_KEY; does not touch the NIM key the live agent uses)
[ "${AGENT_EVAL_PROVIDER:-}" = openai ] || grep -q '^NVIDIA_API_KEY=.' .env || { echo "NVIDIA_API_KEY missing in .env: the evals cannot run, and a skipped eval is not a pass." >&2; exit 1; }
test_database="recepcion_evals_$(date +%s)_$$"
docker compose exec -T db createdb -U recepcion "$test_database"
trap 'docker compose exec -T db dropdb -U recepcion "$test_database"' EXIT
docker run --rm --network recepcion_default --env-file .env -e TEST_DB_NAME="$test_database" -e AGENT_EVAL=1 -e AGENT_EVAL_MODEL="${1:-}" -e AGENT_EVAL_PROVIDER="${AGENT_EVAL_PROVIDER:-}" -e AGENT_EVAL_SPACING="${AGENT_EVAL_SPACING:-}" -e AGENT_EVAL_REASONING="${AGENT_EVAL_REASONING:-}" -e AGENT_EVAL_FILTER="${AGENT_EVAL_FILTER:-}" -v "$PWD:/src" -v recepcion-nuget:/root/.nuget/packages -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'export TEST_DATABASE="Host=db;Database=${TEST_DB_NAME};Username=recepcion;Password=${POSTGRES_PASSWORD}"; dotnet test backend.Tests/Recepcion.Tests.csproj --filter "Category=Eval${AGENT_EVAL_FILTER:+&DisplayName~$AGENT_EVAL_FILTER}" --logger "console;verbosity=detailed"'
