# shellcheck shell=bash
build_ports_report() {
  cat <<EOF
Firewall request for provider node ${PUBLIC_HOST:-<host>}

INBOUND — public (open to the internet):
  27701/tcp   nginx -> node. Playlists / HLS API. End-user devices.
  27703/tcp   nginx -> static .ts segments. The actual video bytes.

INBOUND — tunnel only (over WireGuard, not public):
  22/tcp      SSH for operator deploy/admin.

INBOUND — client side:
  51820/udp   WireGuard, so the operator can peer in.

OUTBOUND (the node must be able to reach):
  tv.play-latino.com:27702   Panel API — token introspect/refresh + activity.
  tv.play-latino.com:27701   Restream source pull — the content path.

NEVER open:
  MySQL/MariaDB — loopback only (127.0.0.1). No firewall rule, ever.
EOF
}
