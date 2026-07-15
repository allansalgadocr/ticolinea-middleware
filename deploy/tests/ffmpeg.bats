#!/usr/bin/env bats
load helpers

setup() { load_lib common.sh; load_lib ffmpeg.sh; }

@test "parses version from ffmpeg -version banner" {
  run ffmpeg_parse_version "ffmpeg version 4.4.2-0ubuntu0.22.04.1 Copyright (c) 2000-2021"
  [ "$status" -eq 0 ]
  [ "$output" = "4.4.2" ]
}

@test "no warning when versions match" {
  run ffmpeg_version_warning "4.4.2" "4.4.2"
  [ "$status" -eq 0 ]
  [ -z "$output" ]
}

@test "warns when versions differ" {
  run ffmpeg_version_warning "6.1.1" "4.4.2"
  [ "$status" -eq 0 ]
  [[ "$output" == *"differs"* ]]
  [[ "$output" == *"6.1.1"* ]]
  [[ "$output" == *"4.4.2"* ]]
}
