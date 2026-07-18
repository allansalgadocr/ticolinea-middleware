# shellcheck shell=bash
CONFIG_REQUIRED_KEYS=(SSH_HOST SSH_USER PROVIDER PROVIDER_NAME PUBLIC_HOST PANEL_API_URL MAIN_FFMPEG_VERSION)

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
  # AUTH_METHOD is optional (not a required key). Read it if present, then set
  # up whichever auth path was chosen. Key auth is the unchanged default.
  AUTH_METHOD="$(config_get_file "$file" AUTH_METHOD || true)"
  config_normalize_auth
  config_load_secrets
  config_setup_password_auth
}

# Normalize AUTH_METHOD to one of {key,password,ask}. "password" reads its
# password from disk-or-prompt; "ask" always prompts and stores nothing; anything
# else (unset, "key", or garbage) is treated as key auth.
config_normalize_auth() {
  case "${AUTH_METHOD:-key}" in
    password) AUTH_METHOD=password ;;
    ask)      AUTH_METHOD=ask ;;
    *)        AUTH_METHOD=key ;;
  esac
  export AUTH_METHOD
}

# Source deploy/secrets/<PROVIDER>.env if present. It may define SSH_PASSWORD /
# SUDO_PASSWORD. Loaded the same safe way as shared.env; absent is fine.
# AUTH_METHOD=ask never touches disk — it skips the file entirely.
config_load_secrets() {
  [ "$AUTH_METHOD" = ask ] && return 0
  local f="$TICO_ROOT/secrets/${PROVIDER}.env"
  [ -f "$f" ] || return 0
  set -a
  # shellcheck disable=SC1090
  source "$f"
  set +a
}

# For password/ask auth, ensure a password is available, expose it to sshpass,
# and require the sshpass binary. No-op under key auth. Never echoes the password.
#  - password: use SSH_PASSWORD from the secrets file if set, else prompt once.
#              SUDO_PASSWORD may come from the file; defaults to SSH_PASSWORD.
#  - ask:      always prompt once (never read from disk), mirror to SUDO_PASSWORD,
#              hold in process env only.
config_setup_password_auth() {
  case "$AUTH_METHOD" in password|ask) ;; *) return 0 ;; esac
  if [ "$AUTH_METHOD" = ask ]; then
    read -rsp "SSH/sudo password for ${SSH_USER}@${SSH_HOST}: " SSH_PASSWORD
    echo >&2
    SUDO_PASSWORD="$SSH_PASSWORD"
  else
    if [ -z "${SSH_PASSWORD:-}" ]; then
      read -rsp "SSH password for ${SSH_USER}@${SSH_HOST}: " SSH_PASSWORD
      echo >&2
    fi
    : "${SUDO_PASSWORD:=$SSH_PASSWORD}"
  fi
  export SSHPASS="$SSH_PASSWORD"
  export SSH_PASSWORD SUDO_PASSWORD
  command -v sshpass >/dev/null 2>&1 || \
    die "sshpass required for AUTH_METHOD=${AUTH_METHOD} — install with: brew install hudochenkov/sshpass/sshpass"
}

_provider_conf() { printf '%s/providers/%s.conf' "$TICO_ROOT" "$1"; }
config_get()  { config_get_file  "$(_provider_conf "$1")" "$2"; }
config_load() { config_load_file "$(_provider_conf "$1")"; }
