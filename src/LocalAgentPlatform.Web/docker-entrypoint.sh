#!/bin/sh
set -eu

# Existing named volumes may have been created by an older root-running image. Repair
# ownership only on the persistent key ring, never on the host bind-mounted workspace.
chown -R "${APP_UID}:${APP_GID}" /var/lib/local-agent-platform/keyring
gosu app sh -c 'find /var/lib/local-agent-platform/keyring -type d -exec chmod 0700 {} + && find /var/lib/local-agent-platform/keyring -type f -exec chmod 0600 {} +'
exec gosu app "$@"
