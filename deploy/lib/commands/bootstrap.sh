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

_assert_ubuntu2204() {
  local v
  # shellcheck disable=SC2016  # single-quoted intentionally: $ID/$VERSION_ID must expand on the remote box, not here
  v="$(remote 'source /etc/os-release; echo "$ID $VERSION_ID"')"
  [ "$v" = "ubuntu 22.04" ] || die "target is '$v'; this tool requires Ubuntu 22.04"
}

_install_packages() {
  log "Installing base packages (idempotent)"
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
  remote 'dotnet --list-runtimes | grep -qi "AspNetCore.*6\." ' \
    || die "aspnetcore-runtime-6.0 not available after install — see RUNBOOK (self-contained fallback)"
}

_create_user_and_dirs() {
  log "Creating service user and directory tree (idempotent)"
  remote_sudo 'bash -s' <<'REMOTE'
set -euo pipefail
id ticolinea >/dev/null 2>&1 || useradd -r -m -d /home/ticolinea -s /usr/sbin/nologin ticolinea
mkdir -p /opt/ticolinea/releases /opt/ticolinea/config /opt/ticolinea/nginx /opt/ticolinea/secrets
mkdir -p /srv/ticolinea/streams /srv/ticolinea/epg /srv/ticolinea/movies /srv/ticolinea/series /srv/ticolinea/raw-movies /srv/ticolinea/logs
chown -R ticolinea:ticolinea /opt/ticolinea /srv/ticolinea
chmod 700 /opt/ticolinea/secrets
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
  local nonloop
  nonloop="$(remote_sudo "ss -ltnH 'sport = :3306' | awk '{print \$4}' | grep -vE '^(127\\.0\\.0\\.1|\\[::1\\]):' || true")"
  [ -z "$nonloop" ] || die "MariaDB is listening on a non-loopback address: $nonloop"
  # Generate DB password once; store on box 0600.
  remote_sudo 'bash -s' <<REMOTE
set -euo pipefail
SECRET=/opt/ticolinea/secrets/db-password
if [ ! -s "\$SECRET" ]; then
  head -c 24 /dev/urandom | base64 | tr -d '/+=' | head -c 32 > "\$SECRET"
  chmod 600 "\$SECRET"; chown ticolinea:ticolinea "\$SECRET"
fi
DBPASS="\$(cat \$SECRET)"
mysql -uroot <<SQL
CREATE DATABASE IF NOT EXISTS \\\`${DB_NAME}\\\`;
CREATE USER IF NOT EXISTS '${DB_USER}'@'127.0.0.1' IDENTIFIED BY '\${DBPASS}';
ALTER USER '${DB_USER}'@'127.0.0.1' IDENTIFIED BY '\${DBPASS}';
GRANT ALL PRIVILEGES ON \\\`${DB_NAME}\\\`.* TO '${DB_USER}'@'127.0.0.1';
FLUSH PRIVILEGES;
SQL
REMOTE
}

_render_and_upload_config() {
  log "Rendering and uploading appsettings + nginx + systemd unit"
  _load_shared_secrets
  export DB_PASSWORD; DB_PASSWORD="$(remote_sudo cat /opt/ticolinea/secrets/db-password)"
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
install -o ticolinea -g ticolinea -m 640 /tmp/appsettings.$PROVIDER.json /opt/ticolinea/config/appsettings.$PROVIDER.json
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

  _assert_ubuntu2204
  _install_packages
  _create_user_and_dirs
  _setup_mariadb
  _render_and_upload_config
  _apply_schema
  log "Bootstrap complete for $PROVIDER. Next: tico deploy $PROVIDER --tag <version>"
}
