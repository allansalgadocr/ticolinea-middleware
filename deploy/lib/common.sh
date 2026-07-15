# shellcheck shell=bash
# Sourced by tico and by tests. No side effects beyond defining functions/vars.
: "${TICO_ROOT:="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"}"

log()  { printf '\033[0;34m[tico]\033[0m %s\n' "$*" >&2; }
warn() { printf '\033[0;33m[warn]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[0;31m[fail]\033[0m %s\n' "$*" >&2; exit 1; }
