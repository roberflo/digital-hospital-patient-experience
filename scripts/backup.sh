#!/bin/sh
set -eu
umask 077
# MR-37: the dump and the keyring go to different destinations, and the keyring leaves encrypted.
# Either one alone decrypts nothing; keep them on different off-host stores. The keyring certificate
# (DATAPROTECTION_CERT_PATH) is never part of either backup. Restore drill: docs/deployment.md.
: "${BACKUP_DB_DIR:?set BACKUP_DB_DIR (destination of the PostgreSQL dump)}"
: "${BACKUP_KEYRING_DIR:?set BACKUP_KEYRING_DIR (destination of the encrypted keyring, not the dump destination)}"
: "${KEYRING_BACKUP_PASSPHRASE:?set KEYRING_BACKUP_PASSPHRASE (stored apart from both backups)}"
mkdir -p "$BACKUP_DB_DIR" "$BACKUP_KEYRING_DIR"
db_dir=$(cd "$BACKUP_DB_DIR" && pwd -P)
keyring_dir=$(cd "$BACKUP_KEYRING_DIR" && pwd -P)
case "$keyring_dir/" in "$db_dir/"*) echo "BACKUP_KEYRING_DIR must not be BACKUP_DB_DIR or inside it" >&2; exit 1;; esac
case "$db_dir/" in "$keyring_dir/"*) echo "BACKUP_DB_DIR must not be inside BACKUP_KEYRING_DIR" >&2; exit 1;; esac
stamp=$(date -u +%Y%m%dT%H%M%SZ)
dump="$db_dir/database-${stamp}.dump"
keyring="$keyring_dir/keyring-${stamp}.tar.gz.enc"
docker compose exec -T db pg_dump -U recepcion -d recepcion -Fc > "$dump.partial"
mv "$dump.partial" "$dump"
docker compose exec -T recepcion-api tar -C /keys -czf - . | openssl enc -aes-256-cbc -pbkdf2 -iter 600000 -salt -pass env:KEYRING_BACKUP_PASSPHRASE -out "$keyring.partial"
# A pipeline only reports openssl's status: decrypt and list the archive so a failed or truncated tar fails here.
openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -pass env:KEYRING_BACKUP_PASSPHRASE -in "$keyring.partial" | tar -tzf - | grep -q '\.xml$' || { echo "keyring backup is empty or unreadable" >&2; rm -f "$keyring.partial"; exit 1; }
mv "$keyring.partial" "$keyring"
printf 'Backup completed:\n  %s\n  %s\n' "$dump" "$keyring"
