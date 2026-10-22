#!/bin/bash
# Run by the db-init service in docker-compose.yml. Builds the database the first time, then runs every
# migration newer than CreateDatabase.sql's baseline (they are written to be safe to run again).
set -euo pipefail

BASELINE="2026-10-16"   # CreateDatabase.sql already includes migrations up to and including this date
sql() { /opt/mssql-tools18/bin/sqlcmd -S db -U sa -P "$SA_PASSWORD" -C -b "$@"; }

sql -Q "IF DB_ID('$DB_NAME') IS NULL CREATE DATABASE [$DB_NAME]"

if [ "$(sql -d "$DB_NAME" -h -1 -W -Q "SET NOCOUNT ON; SELECT CASE WHEN OBJECT_ID('dbo.tblUsers') IS NULL THEN 0 ELSE 1 END")" = "0" ]; then
    echo "Creating the database tables..."
    sql -d "$DB_NAME" -i /database/CreateDatabase.sql > /dev/null
fi

for f in /database/Migrations/*.sql; do
    name=$(basename "$f")
    if [[ "${name:0:10}" > "$BASELINE" ]]; then
        echo "Applying $name"
        sql -d "$DB_NAME" -i "$f" > /dev/null
    fi
done

if [ "${LOAD_SAMPLE_DATA:-0}" = "1" ] && [ -d /database/SampleData ]; then
    for f in /database/SampleData/*.sql; do
        echo "Loading $(basename "$f")"
        sql -d "$DB_NAME" -i "$f"
    done
fi

echo "Database ready."
