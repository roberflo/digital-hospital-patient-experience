#!/bin/sh
set -eu
# A unique disposable database prevents touching application or previous test data.
# Usage: scripts/test-backend.sh [dotnet test filter]. Agent evals call a real model and run
# separately (scripts/eval-agent.sh); the live journey writes to the local Hospital
# (scripts/live-agent.sh). The default filter excludes both.
test_database="recepcion_tests_$(date +%s)_$$"
docker compose exec -T db createdb -U recepcion "$test_database"
trap 'docker compose exec -T db dropdb -U recepcion "$test_database"' EXIT
# Database credentials stay inside the container environment.
docker run --rm --network recepcion_default --env-file .env -e TEST_DB_NAME="$test_database" -e TEST_FILTER="${1:-Category!=Eval&Category!=Live}" -v "$PWD:/src" -v recepcion-nuget:/root/.nuget/packages -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'export TEST_DATABASE="Host=db;Database=${TEST_DB_NAME};Username=recepcion;Password=${POSTGRES_PASSWORD}"; dotnet test backend.Tests/Recepcion.Tests.csproj --filter "$TEST_FILTER" --logger "console;verbosity=minimal"'
