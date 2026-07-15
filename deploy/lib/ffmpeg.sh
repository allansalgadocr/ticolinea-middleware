# shellcheck shell=bash
ffmpeg_parse_version() { # raw banner
  printf '%s' "$1" | grep -oE 'version [0-9]+\.[0-9]+(\.[0-9]+)?' | head -n1 \
    | grep -oE '[0-9]+\.[0-9]+(\.[0-9]+)?'
}

ffmpeg_version_warning() { # client, main
  local client="$1" main="$2"
  [ "$client" = "$main" ] && return 0
  printf 'FFmpeg version on client (%s) differs from main (%s) — HLS muxing behavior may differ; validate streams before go-live.' \
    "$client" "$main"
}
