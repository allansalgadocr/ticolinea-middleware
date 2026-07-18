#!/usr/bin/env bats
load helpers

setup() {
  load_lib common.sh
  load_lib config.sh
  TMP="$(mktemp -d)"
  cat > "$TMP/good.conf" <<'EOF'
SSH_HOST=10.8.0.4
SSH_USER=ubuntu
PROVIDER=acme
PROVIDER_NAME=Acme TV
PUBLIC_HOST=iptv.acme.cr
PANEL_API_URL=http://tv.play-latino.com:27702/api/v2
MAIN_FFMPEG_VERSION=4.4.2
EOF
}
teardown() { rm -rf "$TMP"; }

@test "config_get reads one value without exporting" {
  run config_get_file "$TMP/good.conf" PROVIDER
  [ "$status" -eq 0 ]
  [ "$output" = "acme" ]
}

@test "config_load derives DB_NAME and DB_USER" {
  config_load_file "$TMP/good.conf"
  [ "$DB_NAME" = "acme-streaming" ]
  [ "$DB_USER" = "streamingservice" ]
  [ "$SSH_HOST" = "10.8.0.4" ]
}

@test "config_load dies on missing required key" {
  grep -v '^PROVIDER=' "$TMP/good.conf" > "$TMP/bad.conf"
  run config_load_file "$TMP/bad.conf"
  [ "$status" -ne 0 ]
  [[ "$output" == *"PROVIDER"* ]]
}

@test "config_get ignores comments and blank lines" {
  printf '\n# a comment\nPROVIDER=acme\n' > "$TMP/c.conf"
  run config_get_file "$TMP/c.conf" PROVIDER
  [ "$output" = "acme" ]
}

@test "config_load dies on a present-but-empty required key" {
  sed 's/^PROVIDER=.*/PROVIDER=/' "$TMP/good.conf" > "$TMP/empty.conf"
  run config_load_file "$TMP/empty.conf"
  [ "$status" -ne 0 ]
  [[ "$output" == *"PROVIDER"* ]]
}

@test "config_load rejects a PROVIDER with unsafe characters" {
  sed 's/^PROVIDER=.*/PROVIDER=acme;rm/' "$TMP/good.conf" > "$TMP/unsafe.conf"
  run config_load_file "$TMP/unsafe.conf"
  [ "$status" -ne 0 ]
  [[ "$output" == *"PROVIDER"* ]]
}

@test "AUTH_METHOD defaults to key when the conf omits it" {
  TICO_ROOT="$TMP" config_load_file "$TMP/good.conf"
  [ "$AUTH_METHOD" = "key" ]
}

@test "AUTH_METHOD normalizes a garbage value to key" {
  printf 'AUTH_METHOD=banana\n' >> "$TMP/good.conf"
  TICO_ROOT="$TMP" config_load_file "$TMP/good.conf"
  [ "$AUTH_METHOD" = "key" ]
}

@test "AUTH_METHOD=ask ignores the on-disk secrets file and prompts instead" {
  mkdir -p "$TMP/secrets" "$TMP/bin"
  # Sentinel var proves whether the file was sourced at all; SSH_PASSWORD in the
  # file must never be used by ask mode.
  printf 'SSH_PASSWORD=SHOULD_NOT_BE_USED\nTICO_SENTINEL=leaked\n' > "$TMP/secrets/acme.env"
  printf '#!/usr/bin/env bash\ntrue\n' > "$TMP/bin/sshpass"
  chmod +x "$TMP/bin/sshpass"
  printf 'AUTH_METHOD=ask\n' >> "$TMP/good.conf"
  TICO_ROOT="$TMP"
  PATH="$TMP/bin:$PATH"
  # Feed the (always-on) interactive prompt from stdin so `read` returns without
  # a TTY — this tests file-is-ignored, not the terminal read itself.
  config_load_file "$TMP/good.conf" <<< 'typed-at-prompt'
  [ "$AUTH_METHOD" = "ask" ]
  # The file was never sourced.
  [ -z "${TICO_SENTINEL:-}" ]
  # The password came from the prompt, not the file.
  [ "$SSH_PASSWORD" = "typed-at-prompt" ]
  [ "$SSH_PASSWORD" != "SHOULD_NOT_BE_USED" ]
  [ "$SUDO_PASSWORD" = "typed-at-prompt" ]
  [ "$SSHPASS" = "typed-at-prompt" ]
}

@test "config_load sources deploy/secrets/<slug>.env when present" {
  mkdir -p "$TMP/secrets"
  printf 'SSH_PASSWORD=fromfile\n' > "$TMP/secrets/acme.env"
  TICO_ROOT="$TMP"
  config_load_file "$TMP/good.conf"
  [ "$SSH_PASSWORD" = "fromfile" ]
}

@test "AUTH_METHOD=password loads the password from the secrets file and requires sshpass" {
  mkdir -p "$TMP/secrets" "$TMP/bin"
  printf 'SSH_PASSWORD=pw123\n' > "$TMP/secrets/acme.env"
  printf '#!/usr/bin/env bash\ntrue\n' > "$TMP/bin/sshpass"
  chmod +x "$TMP/bin/sshpass"
  printf 'AUTH_METHOD=password\n' >> "$TMP/good.conf"
  TICO_ROOT="$TMP"
  PATH="$TMP/bin:$PATH"
  config_load_file "$TMP/good.conf"
  [ "$AUTH_METHOD" = "password" ]
  [ "$SSH_PASSWORD" = "pw123" ]
  [ "$SUDO_PASSWORD" = "pw123" ]
  [ "$SSHPASS" = "pw123" ]
}
