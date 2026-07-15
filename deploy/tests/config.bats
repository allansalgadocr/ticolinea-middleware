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
SEGMENT_BASE_URL=http://iptv.acme.cr:27703/
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
