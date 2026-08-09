# shellcheck shell=bash
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/config.sh"
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/remote.sh"
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/commands/deploy.sh"

# `tico update` is `tico deploy` with the artifact hunt done for you: it turns a
# version number into a validated, unpacked release directory and hands that to
# cmd_deploy, which still owns every safety property (preflight, staged swap,
# progress-aware verify, auto-rollback). RUNBOOK sections 7 and 10 describe the
# same sequence by hand; doing it by hand is where operators reach for a stale
# artifact directory or a source-code ZIP that has no schema.sql.

# Pure: the release tag and asset filename CI publishes for a version.
# build-node.yml writes `node-v$ver` / `node-$ver.zip`; keep both here so the
# naming lives in exactly one place on the deploy side.
update_release_tag()   { printf 'node-v%s' "$1"; }
update_release_asset() { printf 'node-%s.zip' "$1"; }

# Pure: the version this checkout would ship. Deploying "whatever VERSION says"
# is the common case — the operator just committed a bump and wants it live.
update_default_version() { tr -d ' \t\n\r' < "$TICO_ROOT/../VERSION"; }

# Pure: owner/repo for `gh`. Derived from origin so a fork or a rename does not
# silently download someone else's release; the literal is only the fallback for
# a checkout with no origin (rare, but `gh` would otherwise guess from cwd).
# Parameter expansion rather than sed -E: BSD sed (every operator on macOS) has
# no non-greedy quantifier, and this has to read both remote forms —
# https://host/owner/repo.git and git@host:owner/repo.
update_repo_slug() {
  local url repo rest owner
  url="$(git -C "$TICO_ROOT/.." remote get-url origin 2>/dev/null || true)"
  [ -n "$url" ] || { printf 'allansalgadocr/ticolinea-middleware'; return 0; }
  url="${url%.git}"; url="${url%/}"
  repo="${url##*/}"
  rest="${url%/*}"
  owner="${rest##*[:/]}"
  printf '%s/%s' "$owner" "$repo"
}

# Pure: a directory is a usable release only if it carries BOTH halves the
# deploy needs. A source-code ZIP has neither; a bare `dotnet publish` output has
# the dll but no schema.sql, and would deploy code against an un-migrated
# database. cmd_deploy re-checks this — the point of checking here is to fail
# before we touch the node at all.
update_validate_artifact() { # dir
  [ -d "${1:-}" ]                            || { warn "artifact directory not found: ${1:-}"; return 1; }
  [ -f "$1/schema.sql" ]                     || { warn "artifact missing schema.sql: $1"; return 1; }
  [ -f "$1/ticolinea.stream.service.dll" ]   || { warn "artifact missing the published dll: $1"; return 1; }
  return 0
}

# Seam: tests stub this so the orchestration is exercised without GitHub.
update_fetch_release() { # tag, asset, dest_dir
  gh release download "$1" --repo "$(update_repo_slug)" --pattern "$2" --dir "$3"
}

# Downloads and unpacks <version> into a fresh directory, printing its path on
# stdout. Everything else this function emits goes to stderr (log/warn/die), so
# the caller can capture the path cleanly.
update_stage_artifact() { # version -> prints artifact dir
  local ver="$1" tag asset workdir
  tag="$(update_release_tag "$ver")"
  asset="$(update_release_asset "$ver")"

  command -v gh >/dev/null 2>&1 \
    || die "the GitHub CLI (gh) is required to download $tag — install it, or pass --artifact <dir>"

  workdir="$(mktemp -d)"
  log "Fetching $tag ($asset) from $(update_repo_slug)"
  if ! update_fetch_release "$tag" "$asset" "$workdir"; then
    rm -rf "$workdir"
    die "could not download $tag — has CI published it yet? (a VERSION bump must be pushed and built first)"
  fi

  local artifact="$workdir/release"
  mkdir -p "$artifact"
  unzip -q "$workdir/$asset" -d "$artifact" || { rm -rf "$workdir"; die "could not unpack $asset"; }

  if ! update_validate_artifact "$artifact"; then
    rm -rf "$workdir"
    die "$tag unpacked into an artifact the deploy cannot use"
  fi

  printf '%s' "$artifact"
}

cmd_update() {
  local slug="" ver="" artifact="" keep_artifact=0 dry=()
  [ $# -ge 1 ] || die "usage: tico update <slug> [--version <v>] [--artifact <dir>] [--dry-run]"
  slug="$1"; shift
  while [ $# -gt 0 ]; do case "$1" in
    --version)  ver="$2"; shift 2;;
    --artifact) artifact="$2"; keep_artifact=1; shift 2;;
    --dry-run)  dry=(--dry-run); shift;;
    *) die "unknown option: $1";;
  esac; done

  [ -n "$ver" ] || ver="$(update_default_version)"
  [ -n "$ver" ] || die "--version is required (and this checkout has no readable VERSION file)"

  config_load "$slug"
  _tico_resolve_paths
  log "Updating $PROVIDER ($SSH_HOST): $(deploy_current_release || echo unknown) -> $ver"

  if [ -n "$artifact" ]; then
    update_validate_artifact "$artifact" || die "unusable --artifact directory: $artifact"
  else
    artifact="$(update_stage_artifact "$ver")"
  fi

  # cmd_deploy owns the swap and the rollback. On failure the staged artifact is
  # deliberately left behind and named: the operator's next move is almost always
  # to re-run the deploy against the same bytes rather than re-download them.
  if cmd_deploy "$slug" --tag "$ver" --artifact "$artifact" "${dry[@]}"; then
    [ "$keep_artifact" -eq 1 ] || rm -rf "$(dirname "$artifact")"
    return 0
  fi

  warn "update failed; the staged artifact is kept at $artifact"
  return 1
}
