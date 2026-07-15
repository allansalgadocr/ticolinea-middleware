# shellcheck shell=bash
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/config.sh"
# shellcheck source=/dev/null
source "$TICO_ROOT/lib/ports.sh"
cmd_ports() {
  [ $# -ge 1 ] || die "usage: tico ports <slug>"
  config_load "$1"
  build_ports_report
}
