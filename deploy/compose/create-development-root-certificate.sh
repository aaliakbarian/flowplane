#!/bin/sh

set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
output=${1:-"$repository_root/deploy/compose/secrets/flowplane-root.pfx"}
output_directory=$(dirname -- "$output")

if [ -e "$output" ]; then
  echo "Refusing to replace existing root certificate: $output" >&2
  exit 1
fi

temporary_directory=$(mktemp -d /tmp/flowplane-root-certificate-XXXXXX)

cleanup() {
  rm -rf "$temporary_directory"
}

trap cleanup EXIT HUP INT TERM
umask 077
mkdir -p "$output_directory"

openssl req \
  -x509 \
  -newkey rsa:3072 \
  -sha256 \
  -nodes \
  -days 730 \
  -subj '/CN=FlowPlane development secret protection' \
  -keyout "$temporary_directory/root.key" \
  -out "$temporary_directory/root.crt"

openssl pkcs12 \
  -export \
  -out "$output" \
  -inkey "$temporary_directory/root.key" \
  -in "$temporary_directory/root.crt" \
  -passout pass:

chmod 0400 "$output"
echo "Created development-only root certificate at $output"
echo "Back it up separately from PostgreSQL data; loss makes managed secrets unrecoverable."
