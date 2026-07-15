# shellcheck shell=bash
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/config.sh"
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/remote.sh"
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/ffmpeg.sh"

cmd_probe() {
  [ $# -ge 1 ] || die "usage: tico probe <slug>"
  config_load "$1"
  log "Probing ${SSH_USER}@${SSH_HOST} (provider: $PROVIDER) — read-only"

  echo "== OS =="
  remote 'cat /etc/os-release | grep -E "^(NAME|VERSION)=" || true'

  echo "== CPU / RAM / Disk =="
  # shellcheck disable=SC2016
  remote 'nproc; free -h | awk "/Mem:/{print \$2\" RAM\"}"; df -h /srv 2>/dev/null || df -h /'

  echo "== ffmpeg =="
  local banner client
  banner="$(remote 'ffmpeg -version 2>/dev/null | head -n1 || echo none')"
  echo "$banner"
  client="$(ffmpeg_parse_version "$banner")"
  if [ -n "$client" ]; then
    local w; w="$(ffmpeg_version_warning "$client" "$MAIN_FFMPEG_VERSION")"
    [ -n "$w" ] && warn "$w"
  else
    warn "ffmpeg not installed (bootstrap will install it)"
  fi

  echo "== aspnetcore-runtime-6.0 =="
  remote 'dotnet --list-runtimes 2>/dev/null | grep -i "AspNetCore.*6\." || echo "not installed (bootstrap will install)"'

  echo "== outbound reachability =="
  remote 'curl -s -o /dev/null -w "panel(27702): %{http_code}\n" --max-time 8 http://tv.play-latino.com:27702/ || echo "panel(27702): UNREACHABLE"'
  remote 'curl -s -o /dev/null -w "origin(27701): %{http_code}\n" --max-time 8 http://tv.play-latino.com:27701/ || echo "origin(27701): UNREACHABLE"'

  log "Probe complete. Nothing was changed."
}
