#!/bin/bash
# Builds a fresh database the way a new install does: CreateDatabase.sql, every newer migration in file name order,
# then every migration a second time (each must be safe to run twice), then the demo portfolio.
# Usage: SQL_PASSWORD=... tests/smoke/build-database.sh [server] [database]
# Needs sqlcmd (mssql-tools18) on the PATH, or run it inside the SQL Server image as CI does (see ci.yml).
set -euo pipefail
cd "$(dirname "$0")/../.."
SERVER="${1:-localhost}"
DB="${2:-PortfolioManagerSmoke}"
BASELINE="2026-10-16"   # CreateDatabase.sql already includes migrations up to and including this date
sql() { sqlcmd -S "$SERVER" -U sa -P "$SQL_PASSWORD" -C -b -I "$@"; }
run() { # run a script, show only its error messages, fail if it raised one
    local out
    if ! out=$(sql -d "$DB" -i "$1" 2>&1) || grep -q '^Msg [0-9]*, Level 1[1-9]' <<<"$out"; then
        echo "FAILED: $1"; grep -A3 '^Msg' <<<"$out" | head -20; exit 1
    fi
}

sql -Q "IF DB_ID('$DB') IS NOT NULL BEGIN ALTER DATABASE [$DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$DB] END; CREATE DATABASE [$DB]"
echo "CreateDatabase.sql"; run Database/CreateDatabase.sql
for pass in 1 2; do
    for f in Database/Migrations/*.sql; do
        name=$(basename "$f")
        [[ "${name:0:10}" > "$BASELINE" ]] || continue
        echo "pass $pass: $name"; run "$f"
    done
done
for f in Database/SampleData/*.sql; do echo "$(basename "$f")"; run "$f"; done
echo "delete-check.sql"; run tests/smoke/delete-check.sql
echo "convert-check.sql"; run tests/smoke/convert-check.sql

# Every stored procedure the code calls must exist
sql -d "$DB" -h -1 -W -Q "SET NOCOUNT ON; SELECT name FROM sys.procedures" | sort -u > /tmp/db-procs.txt
grep -rhoE '"(dbo\.)?usp[A-Za-z0-9_]+"' src --include=*.cs --include=*.cshtml | tr -d '"' | sed 's/^dbo\.//' | sort -u > /tmp/code-procs.txt
missing=$(comm -23 /tmp/code-procs.txt /tmp/db-procs.txt)
if [ -n "$missing" ]; then echo "Procedures called by the code but missing from the database:"; echo "$missing"; exit 1; fi
echo "Database ready: $(wc -l < /tmp/code-procs.txt) procedures used by the code all exist."
