# shellcheck shell=bash
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/config.sh"
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/remote.sh"
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/commands/deploy.sh"

cmd_rollback() {
  [ $# -ge 1 ] || die "usage: tico rollback <slug>"
  config_load "$1"
  _tico_resolve_paths
  local current previous

  # Mirrors deploy_prune_releases' current-detection exactly (readlink -f,
  # tolerant of a missing/broken link) so "current" never diverges between
  # deploy, prune, and rollback.
  current="$(remote "basename \"\$(readlink -f $TICO_CURRENT_LINK 2>/dev/null || true)\" 2>/dev/null || true")"
  [ -n "$current" ] || die "no current release found on $PROVIDER — nothing to roll back from"

  # Newest-first release listing (same idiom deploy.sh uses), minus current,
  # exact-match filtered so a version string can't accidentally substring-match
  # a sibling release (e.g. "1.0" inside "1.10").
  previous="$(remote "ls -1dt $TICO_RELEASES_DIR/*/ 2>/dev/null | xargs -r -n1 basename | grep -vFx '$current' | head -1")"
  [ -n "$previous" ] || die "no previous release found to roll back to"

  log "Rolling back $PROVIDER: $current -> $previous"
  deploy_rollback_to "$previous"
  if [ "$(remote_health)" = "200" ]; then
    log "Rollback healthy."
  else
    warn "Node not healthy after rollback — see RUNBOOK."
  fi
}
