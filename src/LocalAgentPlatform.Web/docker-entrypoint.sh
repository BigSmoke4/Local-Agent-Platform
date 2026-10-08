#!/bin/sh
set -eu

# Existing named volumes may have been created by an older root-running image. Repair
# ownership only on the persistent key ring, never on the host bind-mounted workspace.
chown -R "${APP_UID}:${APP_GID}" /var/lib/local-agent-platform/keyring
exec gosu app "$@"
