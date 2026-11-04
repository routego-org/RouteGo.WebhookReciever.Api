#!/bin/bash
# Prints the X-Signature value for a webhook body: sha256=<hex HMAC-SHA256 of the file's exact bytes>.
# Usage: tools/sign.sh <body-file> [secret]     (secret defaults to $WEBHOOK_SECRET, then the local-only value)
set -e

body_file="${1:?usage: tools/sign.sh <body-file> [secret]}"
secret="${2:-${WEBHOOK_SECRET:-local-webhook-secret}}"

hex=$(openssl dgst -sha256 -hmac "$secret" -hex < "$body_file" | sed 's/^.* //')
echo "sha256=$hex"
