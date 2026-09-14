#!/bin/sh
# Deep Link Engine — run once by the official postgres entrypoint while initialising an EMPTY data
# directory (docker-entrypoint-initdb.d). Adds an include for the mounted tuning file so that
# deploy/postgres/postgresql.tuning.conf becomes part of the server configuration and later edits
# take effect on restart. include_if_exists keeps the server starting if the mount is missing.
#
# The file must have LF line endings (a CRLF shebang fails inside the container).
set -eu

TUNING=/etc/postgresql/tuning.conf
CONF="${PGDATA}/postgresql.conf"

if ! grep -q "include_if_exists = '${TUNING}'" "$CONF"; then
    {
        echo ""
        echo "# Deep Link Engine: sizing and autovacuum settings for a 4 GB host (deploy/postgres)."
        echo "include_if_exists = '${TUNING}'"
    } >> "$CONF"
    echo "dle: added include_if_exists = '${TUNING}' to ${CONF}"
fi
