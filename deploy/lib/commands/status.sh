# shellcheck shell=bash
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/config.sh"
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/remote.sh"
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/commands/deploy.sh"

cmd_status() {
  [ $# -ge 1 ] || die "usage: tico status <slug>"
  config_load "$1"
  _tico_resolve_paths
  echo "provider:      $PROVIDER ($SSH_HOST)"
  echo "health:        $(remote_health) (200=healthy)"
  echo "current:       $(remote "readlink $TICO_CURRENT_LINK 2>/dev/null | xargs -r basename || echo none")"
  echo "unit active:   $(remote 'systemctl is-active ticolinea-streaming 2>/dev/null || echo inactive')"
  # $1 here is awk's own field variable, evaluated remotely — not a local
  # shell expansion. Escaped so it survives this file's own shellcheck pass.
  # shellcheck disable=SC2016
  echo "uptime(s):     $(remote 'systemctl show ticolinea-streaming -p ActiveEnterTimestampMonotonic --value 2>/dev/null | awk "{print int(\$1/1000000)}"')"
  # Distinct channels with a segment in the last minute = channels actually
  # producing. The raw file count (~10 segs/min/channel) reads as "983" on a
  # 98-channel node and confused operators into thinking something was wrong.
  echo "producing:     $(remote_fresh_stream_ids | grep -c . | tr -d ' ') channels ($(remote_fresh_stream_count) segments/min)"
}
