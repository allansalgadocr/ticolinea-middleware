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

# Seams so tests can stub the observations independently of the runner.
# deploy_baseline_ids (last-minute mtime window) is for the PRE-swap baseline
# only; deploy_recovered_ids (post-marker) is what post-swap verification
# consults — see deploy_verify for why the two must not be interchanged.
deploy_health() { remote_health; }
deploy_baseline_ids()  { remote_fresh_stream_ids; }
deploy_recovered_ids() { remote_recovered_stream_ids; }
# Which release does `current` actually resolve to? The identity signal the
# swap must be judged by — health alone can't name the serving release.
deploy_current_release() {
  remote "readlink $TICO_CURRENT_LINK 2>/dev/null | xargs -r basename || true" | tr -d '\r'
}

# Pure, locally-testable verification core: which baseline stream IDs have no
# post-marker segment yet? Args are whitespace/newline-separated ID lists;
# prints the missing IDs one per line (empty output = every channel recovered).
# Matching is exact per-token — padding each side with spaces means ID "5" is
# never satisfied by ID "55"'s segments.
deploy_missing_ids() { # baseline_ids, recovered_ids
  local recovered id
  recovered=" $(printf '%s' "${2:-}" | tr '\n' ' ') "
  # shellcheck disable=SC2086 # word-splitting the ID list is the point
  for id in ${1:-}; do
    case "$recovered" in
      *" $id "*) ;;
      *) printf '%s\n' "$id" ;;
    esac
  done
}

deploy_verify() { # baseline_ids (whitespace-separated; empty => health-only)
  # Empty baseline = the node was serving nothing before the swap: first
  # deploy, or any deploy inside the RUNBOOK spec B window (no channel rows
  # yet), where health alone is the verifiable signal. Same semantics as the
  # old zero-count rule, now set-based. The verify contract remains "streams
  # recovered to baseline", never "streams appeared".
  local baseline="${1:-}"
  # Injectable so tests run instantly; production keeps the ~60s HLS window
  # (12 tries * 5s) unless the caller overrides these.
  local tries="${TICO_VERIFY_TRIES:-12}" interval="${TICO_VERIFY_SLEEP:-5}"
  local attempt=0 code recovered missing total miss_n shown
  # shellcheck disable=SC2086 # intentional word-split to count baseline IDs
  set -- $baseline
  total=$#
  while [ "$attempt" -lt "$tries" ]; do
    code="$(deploy_health)"
    missing=""
    if [ "$code" = "200" ]; then
      [ "$total" -eq 0 ] && return 0
      # Per-channel, marker-based: EVERY baseline ID must have written at
      # least one segment since the restart. An aggregate count is not
      # recovery evidence — fast channels mask a dead one, and with ~6s
      # segments the post-restart observation window (~55s minus startup)
      # can never reproduce a full 60s-window baseline count, so an
      # aggregate gate rolled back healthy deploys by construction.
      recovered="$(deploy_recovered_ids)"
      missing="$(deploy_missing_ids "$baseline" "$recovered")"
      [ -z "$missing" ] && return 0
    fi
    attempt=$((attempt + 1))
    # Without this the verify window is a 60s silence ending in a bare failure —
    # indistinguishable from a hang, and no clue whether health or recovery lost.
    if [ "$code" = "200" ] && [ -n "$missing" ]; then
      miss_n="$(printf '%s\n' "$missing" | grep -c .)"
      # Cap the missing list so one bad deploy of a large node can't flood the log.
      shown="$(printf '%s\n' "$missing" | head -5 | tr '\n' ' ')"
      shown="${shown% }"
      [ "$miss_n" -gt 5 ] && shown="$shown (+$((miss_n - 5)) more)"
      log "verify: health=200 recovered=$((total - miss_n))/${total} missing: ${shown} (attempt ${attempt}/${tries})"
    else
      log "verify: health=${code:-?} recovered=?/${total} (attempt ${attempt}/${tries})"
    fi
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
  # Explicit exit on failure: rollback also runs in set -e-suppressed context
  # (called from the if-condition chain). A silent rollback failure would leave
  # a broken release serving while the operator reads "rolled back".
  remote_sudo 'bash -s' <<REMOTE || die "ROLLBACK FAILED — manual intervention required (see RUNBOOK 'Roll back by hand')"
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
  local new="$1" prev="$2" baseline="${BASELINE_IDS:-}"
  # First deploy (no previous release): health-only regardless of anything on
  # the box — there is no "recovered to baseline" story when nothing this tool
  # deployed was serving. An empty baseline set carries the same rule for
  # redeploys inside the spec B window (node has no channel rows yet):
  # requiring a stream the node never served would fail by construction.
  # The verify contract is "streams recovered to baseline", never "streams
  # appeared".
  [ -z "$prev" ] && baseline=""
  # Over stdin (heredoc), not `bash -c "A && B"` argv — ssh flattens argv and
  # breaks a multi-word `-c` payload. Unquoted heredoc: local vars expand here.
  # The restart MUST precede the marker touch: the old service's ffmpeg
  # children keep writing until systemctl stops the cgroup, so a marker
  # touched before the restart would count the old process's final segments
  # as post-restart evidence. The unit is Type=simple — restart returns once
  # the new process is exec'd, and its first segment lands seconds later, so
  # the new process's output postdates the marker. Worst case its very first
  # segment is ignored and verification waits ~one more segment.
  # EXPLICIT error check: this function is called from an `if` condition, so
  # `set -e` is suppressed for its whole body (bash rule). Without the check,
  # a failed swap (ssh drop, sudo denial) fell through to a health-only verify
  # that happily attested the OLD release still serving — false success
  # (happened in production: 'Permission denied' mid-run, 'Deploy verified').
  if ! remote_sudo 'bash -s' <<REMOTE
set -euo pipefail
ln -sfn $TICO_RELEASES_DIR/$new $TICO_CURRENT_LINK.tmp
mv -T $TICO_CURRENT_LINK.tmp $TICO_CURRENT_LINK
systemctl restart ticolinea-streaming
touch /srv/${PROVIDER}/.tico-deploy-marker
REMOTE
  then
    warn "Swap step FAILED (ssh/sudo error) — the serving release is unchanged. Re-run the deploy."
    return 1
  fi
  # Identity check: health alone can't tell WHICH release is serving (on a
  # channel-less node verify is health-only). The swap is real only if
  # `current` now resolves to the new tag.
  local live; live="$(deploy_current_release)"
  if [ "$live" != "$new" ]; then
    warn "current resolves to '${live:-unknown}', expected '$new' — swap did not take. Re-run the deploy."
    return 1
  fi
  if deploy_verify "$baseline"; then
    log "Deploy $new verified."
    return 0
  fi
  warn "Verification failed for $new."
  # Roll back only if the new release is actually the one serving; when the
  # swap never took, restarting the healthy old release would be pure harm.
  if [ -n "$prev" ] && [ "$(deploy_current_release)" = "$new" ]; then
    deploy_rollback_to "$prev"
  elif [ -n "$prev" ]; then
    warn "serving release is not $new — no rollback performed."
  else
    # Dying here (the old behavior) aborted mid-cleanup with a rollback error
    # while the real state — new release live but unverified — went unreported.
    warn "first deploy: no previous release to roll back to — leaving $new in place"
  fi
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

  # 2. Baseline: the SET of stream IDs with a segment in the last minute, not
  # a count. Verification is per-channel — every one of these IDs must write a
  # post-restart segment — because an aggregate count lets fast channels mask
  # a dead one and can't be reproduced inside the shorter verify window.
  local previous baseline baseline_n
  previous="$(remote "readlink $TICO_CURRENT_LINK 2>/dev/null | xargs -r basename || true")"
  # FAIL CLOSED: a capture failure (ssh drop, permission error, missing
  # streams dir) must never collapse into the legitimate empty set — empty
  # relaxes verification to health-only (spec B), so failing open here would
  # verify a busy node's deploy without checking a single channel. Only a
  # successful capture (exit 0), empty or not, may proceed.
  if ! baseline="$(deploy_baseline_ids)"; then
    die "could not capture active-stream baseline — refusing to deploy without it"
  fi
  export BASELINE_IDS="$baseline"
  # shellcheck disable=SC2086 # intentional word-split to count baseline IDs
  set -- $baseline
  baseline_n=$#
  log "Baseline: previous=${previous:-none}, active streams=${baseline_n}"

  # 3-5. Swap, verify, auto-rollback.
  if deploy_run_swap_and_verify "$tag" "$previous"; then
    deploy_prune_releases
    log "Deploy complete: $PROVIDER now on $tag"
  elif [ -n "$previous" ]; then
    die "Deploy failed and was rolled back to $previous. Investigate before retrying."
  else
    die "First deploy failed verification; $tag was left in place (current -> releases/$tag, service restarted). Inspect with: tico status $slug"
  fi
}
