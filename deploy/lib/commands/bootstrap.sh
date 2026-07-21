# shellcheck shell=bash
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/config.sh"
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/template.sh"
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/remote.sh"

_load_shared_secrets() {
  local f="$TICO_ROOT/secrets/shared.env"
  [ -f "$f" ] || die "missing $f (copy secrets/shared.env.example and fill it)"
  # shellcheck disable=SC1090
  set -a; source "$f"; set +a
  [ -n "${JWT_PUBLIC_KEY:-}" ] || die "shared.env: JWT_PUBLIC_KEY unset"
  [ -n "${PANEL_API_KEY:-}" ] || die "shared.env: PANEL_API_KEY unset"
}

# Pure OS-support predicate over an "<id> <version_id>" string (e.g. "ubuntu 24.04").
# Factored out of _assert_supported_os so the guard is unit-testable with no live host.
_tico_os_supported() {
  case "$1" in
    "ubuntu 22.04"|"ubuntu 24.04") return 0 ;;
    *) return 1 ;;
  esac
}

# Read the target's OS and gate on it. TICO_OS_RELEASE is left in scope (global)
# so _install_packages can branch the .NET 6 install by OS: apt on 22.04, and
# dotnet-install.sh on 24.04 (whose Microsoft feed carries no net6, EOL).
_assert_supported_os() {
  # shellcheck disable=SC2016  # single-quoted intentionally: $ID/$VERSION_ID must expand on the remote box, not here
  TICO_OS_RELEASE="$(remote 'source /etc/os-release; echo "$ID $VERSION_ID"' | tr -d '\r')"
  _tico_os_supported "$TICO_OS_RELEASE" \
    || die "target is '$TICO_OS_RELEASE'; this tool requires Ubuntu 22.04 or 24.04"
}

_disable_swap() {
  log "Disabling swap (idempotent)"
  # A streaming node should never swap — it kills HLS timing. Turn swap off
  # now and comment out any swap line in fstab so it stays off across a
  # reboot. The [^#] guard on the sed means re-running this is a no-op on a
  # box that's already had its fstab swap line commented out.
  remote_sudo 'bash -s' <<'REMOTE'
set -euo pipefail
swapoff -a || true
sed -ri '/\sswap\s/s/^([^#])/#\1/' /etc/fstab
REMOTE
}

# 24.04: Microsoft's 24.04 apt feed carries no .NET 6 (EOL), so install the
# ASP.NET Core 6.0 runtime with the official dotnet-install.sh into the same
# /usr/share/dotnet layout the 22.04 apt feed produces, then symlink it onto
# PATH — so the systemd unit and deploy paths need no per-OS change. Idempotent:
# skip when the runtime is already present. Base packages then install via apt
# exactly as on 22.04 (ffmpeg is 6.x on 24.04, which is expected).
_install_packages_2404() {
  remote_sudo 'bash -s' <<'REMOTE'
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
if ! dotnet --list-runtimes 2>/dev/null | grep -qi 'Microsoft.AspNetCore.App 6\.'; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --runtime aspnetcore --channel 6.0 --install-dir /usr/share/dotnet
  ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
  rm -f /tmp/dotnet-install.sh
fi
apt-get install -y -qq nginx mariadb-server ffmpeg rsync curl
apt-mark hold ffmpeg
REMOTE
}

_install_packages() {
  log "Installing base packages (idempotent)"
  if [ "${TICO_OS_RELEASE:-}" = "ubuntu 24.04" ]; then
    _install_packages_2404
  else
  remote_sudo 'bash -s' <<'REMOTE'
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
if ! dpkg -s aspnetcore-runtime-6.0 >/dev/null 2>&1; then
  wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/ms-prod.deb
  dpkg -i /tmp/ms-prod.deb
  apt-get update -qq
  apt-get install -y -qq aspnetcore-runtime-6.0
fi
apt-get install -y -qq nginx mariadb-server ffmpeg rsync curl
apt-mark hold ffmpeg
REMOTE
  fi
  remote 'dotnet --list-runtimes | grep -qi "AspNetCore.*6\." ' \
    || die "aspnetcore-runtime-6.0 not available after install — see RUNBOOK (self-contained fallback)"
}

_create_user_and_dirs() {
  log "Creating service user and directory tree (idempotent)"
  # Unquoted heredoc (was <<'REMOTE'): ${PROVIDER} must expand here on the
  # controller before the script is sent over ssh — a single-quoted heredoc
  # would ship the literal string "${PROVIDER}" to the remote shell instead.
  # No other $ references live in this block, so unquoting is safe.
  remote_sudo 'bash -s' <<REMOTE
set -euo pipefail
id ticolinea >/dev/null 2>&1 || useradd -r -m -d /home/ticolinea -s /usr/sbin/nologin ticolinea
mkdir -p /opt/${PROVIDER}/releases /opt/${PROVIDER}/config /opt/${PROVIDER}/nginx /opt/${PROVIDER}/secrets
mkdir -p /srv/${PROVIDER}/streams /srv/${PROVIDER}/epg /srv/${PROVIDER}/movies /srv/${PROVIDER}/series /srv/${PROVIDER}/raw-movies /srv/${PROVIDER}/logs
chown -R ticolinea:ticolinea /opt/${PROVIDER} /srv/${PROVIDER}
chmod 700 /opt/${PROVIDER}/secrets
REMOTE
}

_setup_mariadb() {
  log "Configuring MariaDB (loopback) + database + app user (idempotent)"
  # Bind loopback.
  remote_sudo 'bash -s' <<'REMOTE'
set -euo pipefail
sed -ri 's/^#?bind-address\s*=.*/bind-address = 127.0.0.1/' /etc/mysql/mariadb.conf.d/50-server.cnf
systemctl enable mariadb
systemctl restart mariadb
REMOTE
  # Prove the live daemon bound loopback-only (the package postinst may have
  # auto-started it before the sed edit; only the restart above makes it take effect).
  #
  # Use `remote` (NOT remote_sudo): `ss -ltnH` prints the Local Address:Port to
  # any user — root is only needed for the process column we don't ask for. This
  # matters because Ubuntu 24.04's default sudoers has `Defaults use_pty`, which
  # pollutes the *captured* stdout of a password `sudo -S` with policy text and
  # would produce a false-positive non-loopback match here. `tr -d '\r'` strips
  # any CRs an ssh channel may append so the grep/compare stays exact.
  local nonloop
  nonloop="$(remote "ss -ltnH 'sport = :3306' | awk '{print \$4}' | grep -vE '^(127\\.0\\.0\\.1|\\[::1\\]):' || true" | tr -d '\r')"
  [ -z "$nonloop" ] || die "MariaDB is listening on a non-loopback address: $nonloop"
  # Generate the DB password ON THE CONTROLLER, once per run, then push it to the
  # box. It is NEVER read back over a captured sudo (that read had the same
  # use_pty pollution bug and could silently corrupt the password). Charset is
  # kept alphanumeric so the value is safe unescaped in both shell and SQL.
  # Re-running bootstrap therefore ROTATES the DB password: the ALTER USER below,
  # the rewritten secret file, and the appsettings re-rendered in
  # _render_and_upload_config all take the same fresh value in one consistent run.
  : "${DB_PASSWORD:=$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 32)}"
  export DB_PASSWORD
  remote_sudo 'bash -s' <<REMOTE
set -euo pipefail
SECRET=/opt/${PROVIDER}/secrets/db-password
printf '%s' '${DB_PASSWORD}' > "\$SECRET"
chmod 600 "\$SECRET"; chown ticolinea:ticolinea "\$SECRET"
mysql -uroot <<SQL
CREATE DATABASE IF NOT EXISTS \\\`${DB_NAME}\\\`;
CREATE USER IF NOT EXISTS '${DB_USER}'@'127.0.0.1' IDENTIFIED BY '${DB_PASSWORD}';
ALTER USER '${DB_USER}'@'127.0.0.1' IDENTIFIED BY '${DB_PASSWORD}';
GRANT ALL PRIVILEGES ON \\\`${DB_NAME}\\\`.* TO '${DB_USER}'@'127.0.0.1';
FLUSH PRIVILEGES;
SQL
REMOTE
}

# Credential for the node console's bootstrap 'admin' account, rendered into
# appsettings as NodeConsole:SeedPassword. Generated on the controller and
# mirrored into the secrets dir exactly like DB_PASSWORD, so the operator can
# always retrieve it from the box.
#
# CRITICAL difference from DB_PASSWORD: re-running bootstrap does NOT rotate the
# console login. The node consumes SeedPassword only while node_admin_users is
# EMPTY (see ConsoleSchema.SeedFirstAdminAsync) — that is deliberate, so a
# redeploy can never reset a password the owner has since changed. On a node
# whose console is already initialised the rendered value is inert, and the
# secret file below is kept only as a record of what the FIRST password was.
_setup_console_credential() {
  # Honour a value supplied via secrets/<provider>.env; otherwise generate one.
  # Alphanumeric so it stays safe unescaped in shell, SQL and JSON.
  : "${CONSOLE_SEED_PASSWORD:=$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 24)}"
  export CONSOLE_SEED_PASSWORD

  # An operator-supplied value reaches TWO hostile contexts: single-quoted inside
  # the remote `sudo bash` heredoc below, and a JSON string value in appsettings.
  # A lone ' would break out of the remote quoting (command injection as root);
  # a " or \ would produce invalid JSON and a node that cannot start. Restrict to
  # a charset that is inert in both, and enforce the same minimum the console
  # applies to every other password (ConsoleValidation.MinPassword = 12).
  case "$CONSOLE_SEED_PASSWORD" in
    *[!A-Za-z0-9._-]*)
      die "CONSOLE_SEED_PASSWORD may only contain letters, digits, dot, underscore or hyphen (no quotes, spaces or backslashes)" ;;
  esac
  [ "${#CONSOLE_SEED_PASSWORD}" -ge 12 ] \
    || die "CONSOLE_SEED_PASSWORD must be at least 12 characters (the console rejects shorter ones)"
  remote_sudo 'bash -s' <<REMOTE
set -euo pipefail
SECRET=/opt/${PROVIDER}/secrets/console-admin-password
printf '%s' '${CONSOLE_SEED_PASSWORD}' > "\$SECRET"
chmod 600 "\$SECRET"; chown ticolinea:ticolinea "\$SECRET"
REMOTE
}

_render_and_upload_config() {
  log "Rendering and uploading appsettings + nginx + systemd unit"
  _load_shared_secrets
  [ -n "${CONSOLE_SEED_PASSWORD:-}" ] || die "CONSOLE_SEED_PASSWORD not set — _setup_console_credential must run before _render_and_upload_config"
  # DB_PASSWORD is generated and exported by _setup_mariadb, which cmd_bootstrap
  # always runs before this. It is deliberately NOT read back from the box: that
  # read went through a captured `remote_sudo`, which use_pty on Ubuntu 24.04
  # pollutes, silently corrupting the rendered connection string.
  [ -n "${DB_PASSWORD:-}" ] || die "DB_PASSWORD not set — _setup_mariadb must run before _render_and_upload_config"
  local tmp; tmp="$(mktemp -d)"
  render_template "$TICO_ROOT/templates/appsettings.provider.json.tmpl" > "$tmp/appsettings.$PROVIDER.json"
  render_template "$TICO_ROOT/templates/nginx-node.conf.tmpl" > "$tmp/node.conf"
  render_template "$TICO_ROOT/templates/ticolinea-streaming.service.tmpl" > "$tmp/ticolinea-streaming.service"

  push "$tmp/appsettings.$PROVIDER.json" "/tmp/appsettings.$PROVIDER.json"
  push "$tmp/node.conf" "/tmp/node.conf"
  push "$tmp/ticolinea-streaming.service" "/tmp/ticolinea-streaming.service"
  rm -rf "$tmp"

  remote_sudo 'bash -s' <<REMOTE
set -euo pipefail
install -o ticolinea -g ticolinea -m 640 /tmp/appsettings.$PROVIDER.json /opt/${PROVIDER}/config/appsettings.$PROVIDER.json
install -m 644 /tmp/node.conf /etc/nginx/sites-available/ticolinea-node.conf
ln -sfn /etc/nginx/sites-available/ticolinea-node.conf /etc/nginx/sites-enabled/ticolinea-node.conf
rm -f /etc/nginx/sites-enabled/default
install -m 644 /tmp/ticolinea-streaming.service /etc/systemd/system/ticolinea-streaming.service
rm -f /tmp/appsettings.$PROVIDER.json /tmp/node.conf /tmp/ticolinea-streaming.service
nginx -t
systemctl reload nginx
systemctl daemon-reload
systemctl enable ticolinea-streaming
REMOTE
}

_setup_nightly_restart() {
  log "Installing nightly-restart timer (03:00 America/Costa_Rica, idempotent)"
  # A service can't cleanly restart itself from inside (Hangfire would be
  # killing its own process) — systemd is the supervisor, so systemd owns the
  # schedule. Explicit timezone: boxes run UTC, but the restart must land in
  # the dead-audience window in Costa Rica regardless.
  remote_sudo 'bash -s' <<'REMOTE'
set -euo pipefail
cat > /etc/systemd/system/ticolinea-restart.service <<'EOF'
[Unit]
Description=Nightly restart of ticolinea-streaming

[Service]
Type=oneshot
ExecStart=/usr/bin/systemctl restart ticolinea-streaming.service
EOF
cat > /etc/systemd/system/ticolinea-restart.timer <<'EOF'
[Unit]
Description=Restart ticolinea-streaming daily at 03:00 Costa Rica

[Timer]
OnCalendar=*-*-* 03:00:00 America/Costa_Rica
# If the box was down at 03:00, do NOT catch-up-restart at a random
# time after boot — skip that day.
Persistent=false

[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
systemctl enable --now ticolinea-restart.timer
REMOTE
}

_apply_schema() {
  log "Applying schema.sql (idempotent). Provide with --schema or place at releases dir on deploy."
  local schema="${TICO_SCHEMA_FILE:-}"
  if [ -z "$schema" ]; then
    warn "no schema file supplied to bootstrap; deploy will apply the one shipped in the release artifact"
    return 0
  fi
  push "$schema" "/tmp/schema.sql"
  remote_sudo "bash -c 'mysql -uroot ${DB_NAME} < /tmp/schema.sql; ec=\$?; rm -f /tmp/schema.sql; exit \$ec'"
}

cmd_bootstrap() {
  [ $# -ge 1 ] || die "usage: tico bootstrap <slug> [--schema path/to/schema.sql]"
  config_load "$1"; shift || true
  while [ $# -gt 0 ]; do case "$1" in
    --schema) TICO_SCHEMA_FILE="$2"; shift 2;;
    *) die "unknown option: $1";;
  esac; done

  _assert_supported_os
  _disable_swap
  _install_packages
  _create_user_and_dirs
  _setup_mariadb
  _setup_console_credential
  _render_and_upload_config
  _setup_nightly_restart
  _apply_schema
  log "Bootstrap complete for $PROVIDER. Next: tico deploy $PROVIDER --tag <version>"
  # Printed once, at the end, where it cannot scroll past unnoticed. Worded to
  # be true on BOTH a fresh node and a re-bootstrap: on an already-initialised
  # console this value is not the working password (see _setup_console_credential).
  log "Console: https://<host>:27701/admin — user 'admin', first-run password: ${CONSOLE_SEED_PASSWORD}"
  log "         Applies only if this node's console has never been initialised."
  log "         Also stored on the box at /opt/${PROVIDER}/secrets/console-admin-password"
}
