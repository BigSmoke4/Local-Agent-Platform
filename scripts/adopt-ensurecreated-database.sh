#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

fail() {
  printf 'EnsureCreated adoption refused: %s\n' "$1" >&2
  exit 1
}

require_env() {
  local name="$1"
  [[ -n "${!name:-}" ]] || fail "required environment variable $name is not set."
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$repo_root"

require_env LAP_ADOPTION_DATABASE_URL
require_env LAP_ADOPTION_BACKUP_FILE
require_env LAP_ADOPTION_CONFIRM_TARGET_DATABASE
require_env LAP_ADOPTION_CONFIRM_WRITERS_STOPPED

[[ "$LAP_ADOPTION_CONFIRM_WRITERS_STOPPED" == "I_STOPPED_ALL_WRITERS" ]] || \
  fail "stop every web instance, worker, and other database writer, then set LAP_ADOPTION_CONFIRM_WRITERS_STOPPED=I_STOPPED_ALL_WRITERS."

for command_name in dotnet psql pg_dump pg_restore python3 cmp mktemp git; do
  command -v "$command_name" >/dev/null 2>&1 || fail "required command '$command_name' is not installed."
done

migration_dir="$repo_root/src/Shared/Data/Migrations"
python3 - "$migration_dir" <<'PY' || exit 1
from pathlib import Path
import re
import sys

migration_dir = Path(sys.argv[1])
initial = sorted(path for path in migration_dir.glob('*.cs')
                 if re.fullmatch(r'\d{14}_InitialCreate\.cs', path.name))
other = sorted(path for path in migration_dir.glob('*.cs')
               if re.fullmatch(r'\d{14}_[A-Za-z0-9_]+\.cs', path.name)
               and path not in initial)
if len(initial) != 1 or other:
    raise SystemExit(
        'This one-time helper only supports the committed InitialCreate baseline. '
        'It deliberately refuses to mark later migrations (which may contain data transformations) as applied.'
    )
PY
migration_status="$(git -C "$repo_root" status --porcelain=v1 --untracked-files=all --ignored=matching -- src/Shared/Data/Migrations)"
[[ -z "$migration_status" ]] || fail "migration files are modified, staged, or untracked; use the committed InitialCreate migration unchanged."

backup_path="$LAP_ADOPTION_BACKUP_FILE"
[[ "$backup_path" == /* ]] || fail "LAP_ADOPTION_BACKUP_FILE must be an absolute path outside the repository."
backup_dir="$(dirname -- "$backup_path")"
backup_name="$(basename -- "$backup_path")"
[[ -d "$backup_dir" ]] || fail "backup directory does not exist: $backup_dir"
[[ -n "$backup_name" && "$backup_name" != "." && "$backup_name" != ".." ]] || fail "invalid backup file path."
[[ ! -e "$backup_path" && ! -L "$backup_path" ]] || fail "backup file already exists; refusing to overwrite it."
backup_path="$(python3 - "$backup_dir" "$backup_name" "$repo_root" <<'PY'
from pathlib import Path
import os
import sys

parent = Path(sys.argv[1]).resolve(strict=True)
name = sys.argv[2]
root = Path(sys.argv[3]).resolve(strict=True)
path = parent / name
try:
    if os.path.commonpath((str(root), str(path))) == str(root):
        raise SystemExit('backup must be outside the Git repository.')
except ValueError:
    pass
print(path)
PY
)" || fail "backup must be stored outside the Git repository."
[[ ! -e "$backup_path" && ! -L "$backup_path" ]] || fail "backup file already exists; refusing to overwrite it."

work_dir="$(mktemp -d "${TMPDIR:-/tmp}/lap-ensurecreated-adoption.XXXXXX")"
chmod 700 "$work_dir"
target_database=""
target_url=""
admin_url=""
reference_database=""
restore_database=""
reference_created=0
restore_created=0
partial_backup=""

cleanup() {
  local status=$?
  trap - EXIT
  set +e
  if [[ "$reference_created" == "1" ]]; then
    if ! psql --no-psqlrc --set=ON_ERROR_STOP=1 --dbname="$admin_url" \
      --command="DROP DATABASE IF EXISTS \"$reference_database\" WITH (FORCE)" >/dev/null; then
      printf 'WARNING: could not remove temporary reference database %s; inspect it manually before retrying.\n' "$reference_database" >&2
    fi
  fi
  if [[ "$restore_created" == "1" ]]; then
    if ! psql --no-psqlrc --set=ON_ERROR_STOP=1 --dbname="$admin_url" \
      --command="DROP DATABASE IF EXISTS \"$restore_database\" WITH (FORCE)" >/dev/null; then
      printf 'WARNING: could not remove temporary restored database %s; it contains a copy of the target data and must be secured/removed manually.\n' "$restore_database" >&2
    fi
  fi
  if [[ -n "$partial_backup" && -e "$partial_backup" ]]; then rm -f -- "$partial_backup"; fi
  rm -rf -- "$work_dir"
  exit "$status"
}
trap cleanup EXIT

# Keep passwords out of process arguments. Both PostgreSQL URLs are normalized to
# password-free URIs and the decoded credentials are written to a private .pgpass file.
python3 - "$work_dir" <<'PY'
from pathlib import Path
from urllib.parse import parse_qsl, quote, unquote, urlencode, urlsplit, urlunsplit
import os
import re
import sys

target_raw = os.environ['LAP_ADOPTION_DATABASE_URL']
admin_raw = os.environ.get('LAP_ADOPTION_ADMIN_DATABASE_URL') or target_raw
uris = [target_raw, admin_raw]
work = Path(sys.argv[1])
allowed_query = {
    'sslmode', 'sslcert', 'sslkey', 'sslrootcert', 'sslcrl',
    'connect_timeout', 'application_name', 'keepalives',
    'keepalives_idle', 'keepalives_interval', 'keepalives_count',
    'tcp_user_timeout', 'target_session_attrs', 'gssencmode', 'channel_binding'
}
reserved_query = {'host', 'hostaddr', 'port', 'user', 'dbname', 'service', 'servicefile', 'passfile', 'options'}
pgpass_rows = []
parsed_uris = []

for raw in uris:
    try:
        parts = urlsplit(raw)
        port = parts.port if parts.port is not None else 5432
    except ValueError as exc:
        raise SystemExit(f'invalid PostgreSQL connection URI: {exc}')
    if parts.scheme.lower() not in {'postgres', 'postgresql'} or not parts.hostname or not parts.username:
        raise SystemExit('Use a TCP PostgreSQL URI of the form postgresql://user:password@host:port/database.')
    if not 1 <= port <= 65535 or any(c.isspace() or c in '\r\n\0/\\' for c in parts.hostname):
        raise SystemExit('PostgreSQL URI host or port is invalid.')
    if parts.fragment:
        raise SystemExit('PostgreSQL connection URIs must not contain a fragment.')
    username = unquote(parts.username)
    if not username or any(c in username for c in '\r\n\0'):
        raise SystemExit('PostgreSQL URI username is empty or contains an invalid character.')
    raw_database = unquote(parts.path[1:]) if parts.path.startswith('/') else ''
    if not re.fullmatch(r'[A-Za-z0-9_][A-Za-z0-9_.-]{0,62}', raw_database):
        raise SystemExit('PostgreSQL URI must name one simple database in its path.')

    query = parse_qsl(parts.query, keep_blank_values=True)
    query_passwords = [value for key, value in query if key.lower() == 'password']
    if len(query_passwords) > 1 or (parts.password is not None and query_passwords):
        raise SystemExit('Specify the PostgreSQL password only once, in URI user information or one password query parameter.')
    password = unquote(parts.password) if parts.password is not None else (query_passwords[0] if query_passwords else '')
    if any(c in password for c in '\r\n\0'):
        raise SystemExit('PostgreSQL URI password contains an invalid character.')

    safe_query = []
    for key, value in query:
        lowered = key.lower()
        if lowered == 'password':
            continue
        if lowered in reserved_query:
            raise SystemExit(f'URI query parameter {key!r} is not accepted; put host, port, user, and database in the URI authority/path.')
        if lowered not in allowed_query:
            raise SystemExit(f'unsupported PostgreSQL URI query parameter {key!r}; use only documented TLS/connection options.')
        safe_query.append((key, value))

    if '@' not in parts.netloc:
        raise SystemExit('PostgreSQL URI must include a username in its authority.')
    host_port = parts.netloc.rsplit('@', 1)[1]
    safe_netloc = f'{quote(username, safe="")}@{host_port}'
    safe_url = urlunsplit((parts.scheme, safe_netloc, parts.path, urlencode(safe_query), ''))

    host = parts.hostname
    def pass_escape(value):
        return value.replace('\\', '\\\\').replace(':', '\\:')
    pgpass_rows.append(':'.join((pass_escape(host), str(port), '*', pass_escape(username), pass_escape(password))))
    parsed_uris.append((safe_url, raw_database))

# Target URI controls reads, backup, and the final history-table insert. The optional
# admin URI is used only to create/drop temporary shadow databases and inspect sessions.
(work / 'target-url').write_text(parsed_uris[0][0] + '\n', encoding='utf-8')
(work / 'target-database').write_text(parsed_uris[0][1] + '\n', encoding='utf-8')
(work / 'admin-url').write_text(parsed_uris[1][0] + '\n', encoding='utf-8')
(work / 'pgpass').write_text('\n'.join(dict.fromkeys(pgpass_rows)) + '\n', encoding='utf-8')
(work / 'pgpass').chmod(0o600)
PY
unset LAP_ADOPTION_DATABASE_URL LAP_ADOPTION_ADMIN_DATABASE_URL

target_url="$(<"$work_dir/target-url")"
target_database="$(<"$work_dir/target-database")"
admin_url="$(<"$work_dir/admin-url")"
[[ "$LAP_ADOPTION_CONFIRM_TARGET_DATABASE" == "$target_database" ]] || \
  fail "typed database confirmation does not match target '$target_database'. Set LAP_ADOPTION_CONFIRM_TARGET_DATABASE to that exact name."

# Prevent any inherited libpq setting or application connection string from silently
# redirecting connections, changing search_path, or exposing a second credential.
while IFS= read -r variable; do unset "$variable"; done < <(compgen -A variable PG)
unset ConnectionStrings__PlatformDb || true
export PGPASSFILE="$work_dir/pgpass"
export PGCONNECT_TIMEOUT=15

psql_scalar() {
  local uri="$1"
  local sql="$2"
  psql --no-psqlrc --set=ON_ERROR_STOP=1 --tuples-only --no-align \
    --dbname="$uri" --command="$sql"
}

url_for_database() {
  python3 - "$admin_url" "$1" <<'PY'
from urllib.parse import quote, urlsplit, urlunsplit
import re
import sys
parts = urlsplit(sys.argv[1])
name = sys.argv[2]
if not re.fullmatch(r'[A-Za-z0-9_][A-Za-z0-9_.-]{0,62}', name):
    raise SystemExit('invalid generated shadow database name')
print(urlunsplit((parts.scheme, parts.netloc, '/' + quote(name, safe=''), parts.query, '')))
PY
}

assert_no_target_sessions() {
  local active
  active="$(psql_scalar "$admin_url" "SELECT count(*)::text FROM pg_catalog.pg_stat_activity WHERE datname = '$target_database' AND pid <> pg_backend_pid()")"
  [[ "$active" =~ ^0+$ ]] || fail "found $active other connection(s) to '$target_database'; stop every writer/worker/client and retry."
}

assert_history_absent() {
  local exists
  exists="$(psql_scalar "$target_url" "SELECT (pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL)::text")"
  [[ "$exists" == "false" ]] || fail "public.\"__EFMigrationsHistory\" already exists; this is not an untracked EnsureCreated database. No adoption was attempted."
}

# Admin and target URLs must land on the same writable PostgreSQL instance.
server_identity() {
  psql_scalar "$1" "SELECT coalesce(inet_server_addr()::text, '') || E'\t' || coalesce(inet_server_port()::text, '') || E'\t' || current_setting('server_version_num') || E'\t' || pg_is_in_recovery()::text"
}
target_server="$(server_identity "$target_url")"
admin_server="$(server_identity "$admin_url")"
[[ "$target_server" == "$admin_server" ]] || fail "admin and target URIs do not reach the same PostgreSQL server/port/version."
[[ "$target_server" != *$'\ttrue' ]] || fail "the target is on a read-only standby; connect to the writable primary."
IFS=$'\t' read -r _server_address _server_port server_version_num _standby_status <<<"$target_server"
[[ "$server_version_num" =~ ^[0-9]+$ ]] || fail "could not determine the PostgreSQL server version."
(( server_version_num >= 130000 )) || fail "PostgreSQL 13 or newer is required for safe temporary-database cleanup."

actual_database="$(psql_scalar "$target_url" "SELECT current_database()")"
[[ "$actual_database" == "$target_database" ]] || fail "the target URI did not connect to the database named by its path."
target_environment="$(psql_scalar "$target_url" "SELECT coalesce(current_schema(), '') || E'\\t' || count(*)::text FROM pg_catalog.pg_namespace WHERE nspname NOT IN ('public', 'information_schema') AND left(nspname, 3) <> 'pg_'")"
IFS=$'\t' read -r current_schema extra_schema_count <<<"$target_environment"
[[ "$current_schema" == "public" ]] || fail "the target role/database search_path does not resolve to public first."
[[ "$extra_schema_count" =~ ^0+$ ]] || fail "the target database contains non-system schemas beyond public; this baseline helper refuses custom-schema databases."
prepared_transactions="$(psql_scalar "$target_url" "SELECT count(*)::text FROM pg_catalog.pg_prepared_xacts WHERE database = current_database()")"
[[ "$prepared_transactions" =~ ^0+$ ]] || fail "the target has prepared transactions; resolve them and retry during a maintenance window."

# The final history table is created by this login. Require it to be the owner of the
# existing public relations and able to create in public, matching the web app's role.
target_privileges="$(psql_scalar "$target_url" "SELECT has_schema_privilege(current_user, 'public', 'CREATE')::text || E'\t' || count(*) FILTER (WHERE pg_catalog.pg_get_userbyid(c.relowner) <> current_user)::text FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relkind IN ('r', 'S', 'v', 'm', 'f') AND c.relname <> '__EFMigrationsHistory'")"
IFS=$'\t' read -r can_create_public foreign_owned_relations <<<"$target_privileges"
[[ "$can_create_public" == "true" ]] || fail "the target login lacks CREATE privilege on schema public."
[[ "$foreign_owned_relations" =~ ^0+$ ]] || fail "the target login does not own every existing public relation; use the same migration-capable database role as the web app."
assert_history_absent
assert_no_target_sessions

# Connection URI parsing accepted only conservative database names; generated names
# contain no user-supplied data and are recorded only after successful CREATE DATABASE.
random_suffix="$(python3 -c 'import secrets; print(secrets.token_hex(6))')"
reference_database="lap_adopt_ref_${$}_${random_suffix}"
restore_database="lap_adopt_restore_${$}_${random_suffix}"

create_shadow_database() {
  local name="$1"
  psql --no-psqlrc --set=ON_ERROR_STOP=1 --dbname="$admin_url" \
    --command="CREATE DATABASE \"$name\" TEMPLATE template0 ALLOW_CONNECTIONS false" >/dev/null
  psql --no-psqlrc --set=ON_ERROR_STOP=1 --dbname="$admin_url" \
    --command="REVOKE ALL ON DATABASE \"$name\" FROM PUBLIC" >/dev/null
  psql --no-psqlrc --set=ON_ERROR_STOP=1 --dbname="$admin_url" \
    --command="ALTER DATABASE \"$name\" WITH ALLOW_CONNECTIONS true" >/dev/null
}

# Save a no-overwrite, mode-0600 custom-format backup before any validation that could
# lead to a change. pg_restore --list and a full restore into a scratch database follow.
partial_backup="$(mktemp "${backup_path}.partial.XXXXXX")" || fail "cannot create a temporary backup beside the requested destination."
chmod 600 "$partial_backup"
printf 'Creating protected logical backup of database %s ...\n' "$target_database"
pg_dump --format=custom --file="$partial_backup" --dbname="$target_url"
[[ -s "$partial_backup" ]] || fail "pg_dump produced an empty backup."
pg_restore --list "$partial_backup" > "$work_dir/backup-contents.list"
[[ -s "$work_dir/backup-contents.list" ]] || fail "pg_restore could not read the backup archive."
python3 - "$partial_backup" "$backup_path" <<'PY'
import os
import sys
os.link(sys.argv[1], sys.argv[2])  # atomic and refuses to overwrite an existing path
PY
rm -f -- "$partial_backup"
partial_backup=""
printf 'Verified logical backup saved at %s (mode 0600).\n' "$backup_path"

restore_created=1
create_shadow_database "$restore_database"
restore_url="$(url_for_database "$restore_database")"
printf 'Testing the backup by restoring it into a temporary database ...\n'
pg_restore --exit-on-error --no-owner --no-acl --dbname="$restore_url" "$backup_path"

# Build a clean reference schema from the committed migration, not from EnsureCreated.
# This helper intentionally supports only InitialCreate; it will refuse later migrations.
dotnet tool restore
dotnet ef migrations script 0 \
  --context PlatformDbContext \
  --project src/Shared/Data/Shared.Data.csproj \
  --startup-project src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj \
  --output "$work_dir/initial-migration.sql"
[[ -s "$work_dir/initial-migration.sql" ]] || fail "EF did not produce a migration script."
reference_created=1
create_shadow_database "$reference_database"
reference_url="$(url_for_database "$reference_database")"
psql --no-psqlrc --set=ON_ERROR_STOP=1 --dbname="$reference_url" \
  --file="$work_dir/initial-migration.sql" >/dev/null

# Compare the complete public schema (tables, columns, indexes, keys, constraints,
# triggers and other public objects), ignoring only EF's history table in the reference.
dump_public_schema() {
  local uri="$1"
  local output="$2"
  pg_dump --schema-only --no-owner --no-privileges --no-comments --no-security-labels \
    --no-publications --no-subscriptions --schema=public \
    --exclude-table='public."__EFMigrationsHistory"' \
    --file="$output" --dbname="$uri"
}

printf 'Comparing the saved database schema against the committed InitialCreate migration ...\n'
dump_public_schema "$restore_url" "$work_dir/backup-schema.sql"
dump_public_schema "$reference_url" "$work_dir/migration-schema.sql"
dump_public_schema "$target_url" "$work_dir/target-schema-before.sql"
cmp -s "$work_dir/backup-schema.sql" "$work_dir/migration-schema.sql" || \
  fail "the restored backup schema does not exactly match InitialCreate; target was not changed."
cmp -s "$target-schema-before.sql" "$work_dir/backup-schema.sql" || \
  fail "the live target schema differs from the verified backup; target was not changed."

schema_fingerprint="$(python3 - "$work_dir/backup-schema.sql" <<'PY'
import hashlib
import pathlib
import sys
print(hashlib.sha256(pathlib.Path(sys.argv[1]).read_bytes()).hexdigest())
PY
)"

# Read applied migration IDs from the fresh reference DB. The file is tab-delimited;
# EF IDs and product versions are checked before being SQL-quoted for the target.
psql --no-psqlrc --set=ON_ERROR_STOP=1 --tuples-only --no-align --field-separator=$'\t' \
  --dbname="$reference_url" \
  --command='SELECT "MigrationId", "ProductVersion" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId" COLLATE "C"' \
  > "$work_dir/migration-history.tsv"
python3 - "$work_dir/migration-history.tsv" "$work_dir/adopt-history.sql" <<'PY'
from pathlib import Path
import hashlib
import sys

source = Path(sys.argv[1]).read_text(encoding='utf-8')
rows = []
for line in source.splitlines():
    if not line:
        continue
    fields = line.split('\t')
    if len(fields) != 2 or any('\n' in field or '\r' in field or '\0' in field for field in fields):
        raise SystemExit('EF returned malformed migration-history metadata; target was not changed.')
    migration_id, product_version = fields
    if not migration_id or len(migration_id) > 150 or not product_version or len(product_version) > 32:
        raise SystemExit('EF returned out-of-range migration metadata; target was not changed.')
    rows.append((migration_id, product_version))
if len(rows) != 1:
    raise SystemExit('Expected exactly the single InitialCreate migration; target was not changed.')
if len({migration_id for migration_id, _ in rows}) != len(rows):
    raise SystemExit('EF returned duplicate migration IDs; target was not changed.')

def literal(value):
    return "'" + value.replace("'", "''") + "'"

canonical = '\n'.join(migration_id + '\t' + product_version for migration_id, product_version in rows)
expected_hash = hashlib.md5(canonical.encode('utf-8')).hexdigest()
values = ',\n'.join(f'    ({literal(migration_id)}, {literal(product_version)})'
                     for migration_id, product_version in rows)
count = len(rows)
sql = f'''BEGIN;
DO $lap_adopt_precheck$
BEGIN
    IF pg_catalog.to_regclass('public."__EFMigrationsHistory"') IS NOT NULL THEN
        RAISE EXCEPTION 'EF migration history already exists; no adoption was applied.';
    END IF;
END;
$lap_adopt_precheck$;

CREATE TABLE public."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

INSERT INTO public."__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES
{values};

DO $lap_adopt_verify$
DECLARE
    actual_count bigint;
    actual_hash text;
BEGIN
    SELECT count(*), md5(coalesce(string_agg("MigrationId" || E'\\t' || "ProductVersion", E'\\n' ORDER BY "MigrationId" COLLATE "C"), ''))
      INTO actual_count, actual_hash
      FROM public."__EFMigrationsHistory";
    IF actual_count <> {count} OR actual_hash IS DISTINCT FROM '{expected_hash}' THEN
        RAISE EXCEPTION 'EF migration-history verification failed; adoption transaction rolled back.';
    END IF;
END;
$lap_adopt_verify$;
COMMIT;
'''
Path(sys.argv[2]).write_text(sql, encoding='utf-8')
PY

# Recheck immediately before the only target write. If anything changed, abort with the
# verified backup left in place. The DDL/row insert itself is one transaction.
assert_no_target_sessions
assert_history_absent
dump_public_schema "$target_url" "$work_dir/target-schema-immediately-before.sql"
cmp -s "$work_dir/target-schema-before.sql" "$work_dir/target-schema-immediately-before.sql" || \
  fail "target schema changed during validation; no migration-history row was inserted."
printf 'Schema matches (SHA-256 %s). Applying only the EF migration-history baseline transactionally ...\n' "$schema_fingerprint"
psql --no-psqlrc --set=ON_ERROR_STOP=1 --dbname="$target_url" --file="$work_dir/adopt-history.sql"

# Verify the exact migration ID/product-version set and that the application schema stayed
# unchanged. No application table or row is rewritten by this procedure.
psql --no-psqlrc --set=ON_ERROR_STOP=1 --tuples-only --no-align --field-separator=$'\t' \
  --dbname="$target_url" \
  --command='SELECT "MigrationId", "ProductVersion" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId" COLLATE "C"' \
  > "$work_dir/target-history.tsv"
cmp -s "$work_dir/migration-history.tsv" "$work_dir/target-history.tsv" || \
  fail "post-adoption EF history rows differ from the reference; keep the backup and investigate before starting the app."
dump_public_schema "$target_url" "$work_dir/target-schema-after.sql"
cmp -s "$work_dir/target-schema-before.sql" "$work_dir/target-schema-after.sql" || \
  fail "post-adoption application schema changed unexpectedly; keep the backup and investigate before starting the app."

printf 'Adoption completed for %s. Verified backup: %s\n' "$target_database" "$backup_path"
printf 'Only public."__EFMigrationsHistory" was added, with the exact InitialCreate row. Start the application and verify readiness before restoring normal traffic.\n'
