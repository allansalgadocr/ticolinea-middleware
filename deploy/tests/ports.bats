#!/usr/bin/env bats
load helpers

setup() { load_lib common.sh; load_lib ports.sh; }

@test "ports report names the public inbound ports and the outbound origin" {
  PUBLIC_HOST=iptv.acme.cr run build_ports_report
  [ "$status" -eq 0 ]
  [[ "$output" == *"27701/tcp"* ]]
  [[ "$output" == *"27703/tcp"* ]]
  [[ "$output" == *"22/tcp"* ]]
  [[ "$output" == *"51820/udp"* ]]
  [[ "$output" == *"tv.play-latino.com:27702"* ]]
  [[ "$output" == *"tv.play-latino.com:27701"* ]]
  [[ "$output" == *"MySQL"* ]]
}
