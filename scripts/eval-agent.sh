#!/bin/sh
set -eu
# Runs the agent evals (docs/reception-agent.md, criterio 8): the real AgentRuntime against the
# configured model, with synthetic patients and simulated Hospital/WhatsApp. Consumes model quota:
# the live agent uses the same key, so never run this while patients are being attended.
# Usage: scripts/eval-agent.sh [model]   e.g. scripts/eval-agent.sh nvidia/nemotron-3-ultra-550b-a55b
#        AGENT_EVAL_FILTER=citas scripts/eval-agent.sh        (one area or one case; a full pass takes about half an hour)
#        AGENT_EVAL_PROVIDER=openai AGENT_EVAL_REASONING=none AGENT_EVAL_SPACING=0.3 scripts/eval-agent.sh gpt-6-luna
#          (grades an OpenAI model with OPENAI_API_KEY; does not touch the NIM key the live agent uses)
# Prints «Evals: N/M pasaron» and exits 1 if a case fails that evals/agent/known-failures.txt does not list, or if no case ran (MR-34).
[ "${AGENT_EVAL_PROVIDER:-}" = openai ] || grep -q '^NVIDIA_API_KEY=.' .env || { echo "NVIDIA_API_KEY missing in .env: the evals cannot run, and a skipped eval is not a pass." >&2; exit 1; }
test_database="recepcion_evals_$(date +%s)_$$"
docker compose exec -T db createdb -U recepcion "$test_database"
log="$(mktemp)"
trap 'docker compose exec -T db dropdb -U recepcion "$test_database"; rm -f "$log" "$log.status"' EXIT
# The exit code of dotnet test is kept: without pipefail a pipe reports tee's, and a run cut short would look green.
{ status=0; docker run --rm --network recepcion_default --env-file .env -e TEST_DB_NAME="$test_database" -e AGENT_EVAL=1 -e AGENT_EVAL_MODEL="${1:-}" -e AGENT_EVAL_PROVIDER="${AGENT_EVAL_PROVIDER:-}" -e AGENT_EVAL_SPACING="${AGENT_EVAL_SPACING:-}" -e AGENT_EVAL_REASONING="${AGENT_EVAL_REASONING:-}" -e AGENT_EVAL_FILTER="${AGENT_EVAL_FILTER:-}" -v "$PWD:/src" -v recepcion-nuget:/root/.nuget/packages -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'export TEST_DATABASE="Host=db;Database=${TEST_DB_NAME};Username=recepcion;Password=${POSTGRES_PASSWORD}"; dotnet test backend.Tests/Recepcion.Tests.csproj --filter "Category=Eval${AGENT_EVAL_FILTER:+&DisplayName~$AGENT_EVAL_FILTER}" --logger "console;verbosity=detailed"' || status=$?; echo "$status" > "$log.status"; } 2>&1 | tee "$log"
# MR-34: N/M, and a failure outside evals/agent/known-failures.txt fails the run. So does a run that did not finish: aborted, without
# its summary, with fewer results than the summary counts, or with dotnet test failing and no case failed (a build error, a crash).
status=$(cat "$log.status" 2>/dev/null || echo 1)
passed=$(grep -cE '^[[:space:]]*Passed AgentEvals\.Case\(' "$log" || true)
failed=$(sed -nE 's/^[[:space:]]*Failed AgentEvals\.Case\(id: "([^"]+)"\).*/\1/p' "$log" | sort -u)
nfailed=$(printf '%s' "$failed" | grep -c . || true)
total=$(sed -nE 's/^Total tests: ([0-9]+).*/\1/p' "$log" | tail -1)
echo "Evals: $passed/${total:-?} pasaron"
if grep -q "Test Run Aborted" "$log"; then echo "La pasada se abortó: no midió todos los casos." >&2; exit 1; fi
[ -n "$total" ] && [ "$total" -gt 0 ] || { echo "Sin resumen de dotnet test o ningún caso corrió: la pasada no midió nada." >&2; exit 1; }
[ $((passed + nfailed)) -eq "$total" ] || { echo "Resultados por caso ($((passed + nfailed))) distintos del total ($total): la pasada no terminó." >&2; exit 1; }
[ "$status" -eq 0 ] || [ "$nfailed" -gt 0 ] || { echo "dotnet test salió con $status sin ningún caso fallido." >&2; exit 1; }
unexpected=""
for id in $failed; do
  if grep -qE "^$id( |\$)" evals/agent/known-failures.txt; then echo "Fallo conocido: $id"; else unexpected="$unexpected $id"; fi
done
[ -z "$unexpected" ] || { echo "Fallos fuera de evals/agent/known-failures.txt:$unexpected" >&2; exit 1; }
