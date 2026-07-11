#!/usr/bin/env bash
# B02 verification: both databases accept a real query. Fails before compose is up, passes after.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Load .env (MSSQL_SA_PASSWORD, ORACLE_PASSWORD, ...)
if [ -f "$ROOT/.env" ]; then
  set -a; . "$ROOT/.env"; set +a
else
  echo "No .env found — copy .env.example to .env first." >&2
  exit 2
fi

echo "Checking databases..."
# Ephemeral pure-Python clients; no host DB drivers needed.
uv run --no-project --quiet --with python-tds --with oracledb \
  python "$ROOT/scripts/check_dbs.py"
echo "Both databases reachable."
