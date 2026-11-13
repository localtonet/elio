#!/bin/sh
# Makes the key that releases of Elio are signed with, so that copies of Elio can install updates from them.
#
#   sh build/update-key.sh <file to write the private key to>
#
# The private half is written to the file you name, and nowhere else. The public half is added to
# src/Elio.Core/update-keys.txt, which is built into every copy of Elio. Needs openssl.
#
# What to do with the private key: it is the one thing that stands between the people who use Elio and a
# false update, so it goes into the repository secret UPDATE_SIGNING_KEY and into a password manager, and the
# file is deleted. It is never committed, and it is not needed anywhere else.
set -eu

OUT=${1:-}
if [ -z "$OUT" ]; then
    echo "Usage: sh build/update-key.sh <file to write the private key to>" >&2
    exit 1
fi
if [ -e "$OUT" ]; then
    echo "$OUT exists already, and a key is never overwritten." >&2
    exit 1
fi

ROOT=$(cd "$(dirname "$0")/.." && pwd)
KEYS=${ELIO_UPDATE_KEYS_FILE:-$ROOT/src/Elio.Core/update-keys.txt}

umask 077
openssl ecparam -name prime256v1 -genkey -noout -out "$OUT"
PUBLIC=$(openssl ec -in "$OUT" -pubout -outform DER 2>/dev/null | base64 | tr -d '\r\n ')

printf '\n# Added %s\n%s\n' "$(date -u +%Y-%m-%d)" "$PUBLIC" >> "$KEYS"

cat <<EOF
Made a signing key.

  Private key: $OUT
  Public key:  added to $KEYS

Next:
  1. Put the contents of $OUT in the repository secret UPDATE_SIGNING_KEY:
       gh secret set UPDATE_SIGNING_KEY < "$OUT"
     (or Settings > Secrets and variables > Actions > New repository secret).
  2. Keep a copy of $OUT in a password manager, then delete the file.
  3. Commit src/Elio.Core/update-keys.txt. Releases built from then on trust the key.
EOF
