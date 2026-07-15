# shellcheck shell=bash
CONFIG_REQUIRED_KEYS=(SSH_HOST SSH_USER PROVIDER PROVIDER_NAME PUBLIC_HOST SEGMENT_BASE_URL PANEL_API_URL MAIN_FFMPEG_VERSION)

config_get_file() { # file, key
  local file="$1" key="$2"
  [ -f "$file" ] || die "config not found: $file"
  # Match KEY=..., ignore comments/blanks, take value after first '='.
  local line
  line="$(grep -E "^${key}=" "$file" | head -n1)" || true
  [ -n "$line" ] || return 1
  [ -n "${line#*=}" ] || return 1
  printf '%s' "${line#*=}"
}

config_load_file() { # file  -> exports all keys + derived
  local file="$1" key val
  [ -f "$file" ] || die "config not found: $file"
  for key in "${CONFIG_REQUIRED_KEYS[@]}"; do
    if ! val="$(config_get_file "$file" "$key")"; then
      die "config $file: missing required key $key"
    fi
    export "$key=$val"
  done
  export DB_NAME="${PROVIDER}-streaming"
  export DB_USER="streamingservice"
  case "$PROVIDER" in
    ""|*[!a-z0-9-]*) die "config: PROVIDER must be a lowercase slug matching [a-z0-9-]: '$PROVIDER'";;
  esac
}

_provider_conf() { printf '%s/providers/%s.conf' "$TICO_ROOT" "$1"; }
config_get()  { config_get_file  "$(_provider_conf "$1")" "$2"; }
config_load() { config_load_file "$(_provider_conf "$1")"; }
