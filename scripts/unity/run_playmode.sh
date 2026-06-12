#!/usr/bin/env bash
set -euo pipefail

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

if ! parse_args "$@"; then
  usage "run_playmode.sh" "PlayMode" >&2
  exit 1
fi
if [[ "$HELP" == "1" ]]; then
  usage "run_playmode.sh" "PlayMode"
  exit 0
fi

run_unity_tests PlayMode
