#!/bin/sh
set -eu
umask 077
# MR-37: generates the local keyring certificate OUTSIDE the repository. Use it with
# docker-compose.keyring.yml (DATAPROTECTION_CERT_FILE=${HOME}/.config/recepcion/dataprotection.p12).
# Losing this file (or its password) makes every key written while it was mounted unreadable.
: "${DATAPROTECTION_CERT_PASSWORD:?export DATAPROTECTION_CERT_PASSWORD first}"
dir="${HOME}/.config/recepcion"
out="$dir/dataprotection.p12"
[ ! -e "$out" ] || { echo "$out already exists; replacing it would orphan the keys it protects" >&2; exit 1; }
mkdir -p "$dir"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes -subj "/CN=recepcion-dataprotection" -keyout "$tmp/key.pem" -out "$tmp/cert.pem" 2>/dev/null
openssl pkcs12 -export -inkey "$tmp/key.pem" -in "$tmp/cert.pem" -keypbe AES-256-CBC -certpbe AES-256-CBC -macalg sha256 -passout env:DATAPROTECTION_CERT_PASSWORD -out "$out"
printf 'Certificate written to %s\n' "$out"
