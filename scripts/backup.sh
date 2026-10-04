#!/bin/sh
set -eu
umask 077
mkdir -p backups
stamp=$(date -u +%Y%m%dT%H%M%SZ)
docker compose exec -T db pg_dump -U recepcion -d recepcion -Fc > "backups/database-${stamp}.dump"
docker compose exec -T recepcion-api tar -C /keys -czf - . > "backups/keyring-${stamp}.tar.gz"
# The database backup is unreadable without the keyring. Store both encrypted off-host.
printf 'Backup completed: backups/*-%s.*\n' "$stamp"
