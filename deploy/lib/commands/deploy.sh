# shellcheck shell=bash
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/config.sh"
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/remote.sh"

# TICO_RELEASES_DIR/TICO_CURRENT_LINK are namespaced by provider slug, so they
# cannot be assigned at source time (PROVIDER isn't set until config_load
# runs). Call this immediately after config_load in every command that needs
# them — cmd_deploy here, plus cmd_rollback/cmd_status which source this file
# for TICO_RELEASES_DIR/TICO_CURRENT_LINK but run their own config_load.
_tico_resolve_paths() {
  TICO_RELEASES_DIR="/opt/${PROVIDER}/releases"
  TICO_CURRENT_LINK="/opt/${PROVIDER}/current"
}

# Seams so tests can stub the two observations independently of the runner.
deploy_health() { remote_health; }
deploy_fresh()  { remote_fresh_stream_count; }

deploy_verify() { # baseline_fresh_count
  local baseline="${1:-0}"
  # Injectable so tests run instantly; production keeps the ~60s HLS window
  # (12 tries * 5s) unless the caller overrides these.
  local tries="${TICO_VERIFY_TRIES:-12}" interval="${TICO_VERIFY_SLEEP:-5}"
  local attempt=0 code fresh need
  while [ "$attempt" -lt "$tries" ]; do
    code="$(deploy_health)"
    if [ "$code" = "200" ]; then
      fresh="$(deploy_fresh)"
      need=1
      [ "${baseline:-0}" -gt 1 ] 2>/dev/null && need="$baseline"
      if [ "${fresh:-0}" -ge "$need" ]; then return 0; fi
    fi
    attempt=$((attempt + 1))
    [ "$attempt" -lt "$tries" ] && sleep "$interval"
  done
  return 1
}

deploy_rollback_to() { # previous_tag
  local prev="$1"
  [ -n "$prev" ] || die "no previous release to roll back to"
  warn "Rolling back to $prev"
  # Sent over stdin (heredoc), never as `bash -c "A && B"` argv: real ssh flattens
  # argv into one space-joined string, so a multi-word `-c` payload loses its
  # quoting and only the first word reaches `bash -c`. Unquoted heredoc so the
  # LOCAL vars ($TICO_RELEASES_DIR/$prev/$TICO_CURRENT_LINK) expand here; no
  # remote-shell var is referenced, so nothing needs escaping.
  remote_sudo 'bash -s' <<REMOTE
set -euo pipefail
ln -sfn $TICO_RELEASES_DIR/$prev $TICO_CURRENT_LINK.tmp
mv -T $TICO_CURRENT_LINK.tmp $TICO_CURRENT_LINK
systemctl restart ticolinea-streaming
REMOTE
}

# Pure, locally-testable selection: given newest-first release basenames on
# stdin and the basename `current` resolves to as arg1, echo which releases to
# prune — everything past the newest 5, NEVER the current one. Keeping this
# free of any remote I/O lets bats exercise the safety-critical rule directly.
# stdin: release basenames, newest-first (one per line). arg1: basename `current` points at.
deploy_select_prunable() {
  local current="$1" i=0 name
  while IFS= read -r name; do
    i=$((i+1))
    [ "$i" -le 5 ] && continue
    [ "$name" = "$current" ] && continue
    printf '%s\n' "$name"
  done
}

deploy_prune_releases() { # keep the 5 most-recent releases, plus whichever one is live
  # A pure mtime "keep last 5" is unsafe on its own: after a rollback, the
  # release `current` points at stops getting a fresh mtime, so enough later
  # deploys can push it out of the top-5 window and rm -rf it out from under
  # a running (or about-to-be-restarted) node. Selection is computed here on
  # the controller (via the pure deploy_select_prunable) so it stays testable;
  # only the targeted deletes run remotely.
  local current listing name
  current="$(remote "basename \"\$(readlink -f $TICO_CURRENT_LINK 2>/dev/null || true)\" 2>/dev/null || true")"
  listing="$(remote "ls -1dt $TICO_RELEASES_DIR/*/ 2>/dev/null | xargs -r -n1 basename || true")"
  while IFS= read -r name; do
    [ -n "$name" ] || continue
    remote_sudo rm -rf "$TICO_RELEASES_DIR/$name"
  done < <(printf '%s\n' "$listing" | deploy_select_prunable "$current")
}

deploy_run_swap_and_verify() { # new_tag, previous_tag
  local new="$1" prev="$2" baseline="${BASELINE_FRESH:-0}"
  # Over stdin (heredoc), not `bash -c "A && B"` argv — ssh flattens argv and
  # breaks a multi-word `-c` payload. Unquoted heredoc: local vars expand here.
  remote_sudo 'bash -s' <<REMOTE
set -euo pipefail
ln -sfn $TICO_RELEASES_DIR/$new $TICO_CURRENT_LINK.tmp
mv -T $TICO_CURRENT_LINK.tmp $TICO_CURRENT_LINK
systemctl restart ticolinea-streaming
REMOTE
  if deploy_verify "$baseline"; then
    log "Deploy $new verified."
    return 0
  fi
  warn "Verification failed for $new."
  deploy_rollback_to "$prev"
  return 1
}

cmd_deploy() {
  local slug="" tag="" artifact="" dry=0
  [ $# -ge 1 ] || die "usage: tico deploy <slug> --tag <v> [--artifact dir] [--dry-run]"
  slug="$1"; shift
  while [ $# -gt 0 ]; do case "$1" in
    --tag) tag="$2"; shift 2;;
    --artifact) artifact="$2"; shift 2;;
    --dry-run) dry=1; shift;;
    *) die "unknown option: $1";;
  esac; done
  [ -n "$tag" ] || die "--tag is required"
  config_load "$slug"
  _tico_resolve_paths

  # 1. Preflight — non-destructive, no downtime for the currently-serving release.
  log "Preflight: health, disk, staging release $tag"
  # The unhealthy-node refusal must never block a --dry-run preview: dry-run
  # changes nothing, so an operator must be able to inspect the plan regardless
  # of the node's current health.
  if [ "$dry" -eq 0 ] && [ "$(deploy_health)" != "200" ]; then
    if remote 'systemctl is-active ticolinea-streaming' >/dev/null 2>&1; then
      die "node is currently unhealthy — refusing to deploy onto a broken node"
    else
      log "no running node yet (first deploy)"
    fi
  fi
  remote 'df --output=pcent /srv | tail -1 | tr -dc 0-9' | awk '{if ($1+0 > 90) exit 1}' \
    || die "disk on /srv is >90% full"

  [ -n "$artifact" ] || die "--artifact <dir> (unpacked release) is required"
  [ -d "$artifact" ] || die "artifact directory not found: $artifact"
  [ -f "$artifact/schema.sql" ] || die "artifact missing schema.sql"
  [ -f "$artifact/ticolinea.stream.service.dll" ] || die "artifact missing the published dll"

  if [ "$dry" -eq 1 ]; then
    log "[dry-run] would rsync $artifact -> $TICO_RELEASES_DIR/$tag and swap"
    return 0
  fi

  # Stage the artifact into releases/<tag> while the old one still serves.
  # The per-provider appsettings — rendered once at bootstrap time from the
  # template, never copied from another provider's config — is layered in
  # here from /opt/${PROVIDER}/config, matching the app's documented config
  # load order (appsettings.json -> appsettings.{ENV}.json ->
  # appsettings.{PROVIDER}.json).
  push "$artifact/" "/tmp/release-$tag/"
  # Over stdin (heredoc), not `bash -c "A && B && ..."` argv — ssh flattens argv
  # and only the first word survives as the `-c` payload. Unquoted heredoc so
  # $TICO_RELEASES_DIR/$tag/$PROVIDER all expand here on the controller.
  remote_sudo 'bash -s' <<REMOTE
set -euo pipefail
mkdir -p $TICO_RELEASES_DIR/$tag
cp -a /tmp/release-$tag/. $TICO_RELEASES_DIR/$tag/
rm -rf /tmp/release-$tag
cp /opt/${PROVIDER}/config/appsettings.$PROVIDER.json $TICO_RELEASES_DIR/$tag/appsettings.$PROVIDER.json
chown -R ticolinea:ticolinea $TICO_RELEASES_DIR/$tag
REMOTE

  # SECURITY: `dotnet publish` force-copies the main node's own configs into
  # the build output (the .csproj copies appsettings.main.json etc.), so the
  # artifact carries Ticolinea's LIVE prod RDS password + panel API key in
  # plaintext. The node runs with PROVIDER=<slug> and never loads them, but
  # they must never sit on a client's disk. Strip every non-provider config
  # from the staged release before it can be swapped in. Keep only the
  # provider-scoped files (appsettings.$PROVIDER.json / appsettings.Production.json).
  remote_sudo rm -f \
    "$TICO_RELEASES_DIR/$tag/appsettings.main.json" \
    "$TICO_RELEASES_DIR/$tag/appsettings.fibraencasa.json" \
    "$TICO_RELEASES_DIR/$tag/appsettings.Development.json"

  # Apply schema (idempotent) before swapping traffic. Over stdin (heredoc), not
  # `bash -c "mysql ... < ..."` argv: ssh flattens argv, so the `<` redirect and
  # path would be re-parsed by the wrong shell. Unquoted heredoc: local vars expand.
  remote_sudo 'bash -s' <<REMOTE
set -euo pipefail
mysql -uroot ${DB_NAME} < $TICO_RELEASES_DIR/$tag/schema.sql
REMOTE

  # 2. Baseline.
  local previous baseline
  previous="$(remote "readlink $TICO_CURRENT_LINK 2>/dev/null | xargs -r basename || true")"
  baseline="$(deploy_fresh || echo 0)"; export BASELINE_FRESH="$baseline"
  log "Baseline: previous=${previous:-none}, fresh streams=${baseline}"

  # 3-5. Swap, verify, auto-rollback.
  if deploy_run_swap_and_verify "$tag" "$previous"; then
    deploy_prune_releases
    log "Deploy complete: $PROVIDER now on $tag"
  else
    die "Deploy failed and was rolled back to ${previous:-none}. Investigate before retrying."
  fi
}
