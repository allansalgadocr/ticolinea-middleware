# shellcheck shell=bash
# Renders ${VAR} placeholders from the environment in a single left-to-right
# pass. Fails if any referenced var is unset OR empty. Values are inserted
# literally — no re-scanning and no pattern-replacement metacharacter surprises.
render_template() { # template-path
  local tmpl="$1"
  [ -f "$tmpl" ] || die "template not found: $tmpl"

  local names name missing=()
  names="$(grep -oE '\$\{[A-Z_][A-Z0-9_]*\}' "$tmpl" | sed -E 's/\$\{([A-Z0-9_]+)\}/\1/' | sort -u)"
  for name in $names; do
    [ -n "${!name:-}" ] || missing+=("$name")
  done
  if [ "${#missing[@]}" -gt 0 ]; then
    die "template $tmpl: unset or empty variables: ${missing[*]}"
  fi

  local content rest out="" pre match var
  content="$(cat "$tmpl")"
  rest="$content"
  local re='\$\{([A-Z_][A-Z0-9_]*)\}'
  while [[ "$rest" =~ $re ]]; do
    match="${BASH_REMATCH[0]}"
    var="${BASH_REMATCH[1]}"
    pre="${rest%%"$match"*}"
    out+="$pre${!var}"
    rest="${rest#*"$match"}"
  done
  out+="$rest"
  printf '%s\n' "$out"
}
