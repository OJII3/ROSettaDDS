#!/usr/bin/env bash
# tools/ros2-perf-helper を colcon で build する。
# nix develop / direnv 環境で実行する前提。
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HELPER_DIR="$ROOT_DIR/tools/ros2-perf-helper"
BUILD_JOBS="${ROS2_PERF_HELPER_BUILD_JOBS:-$(nproc 2>/dev/null || echo 1)}"

if [[ ! -d "$HELPER_DIR" ]]; then
  echo "helper dir not found: $HELPER_DIR" >&2
  exit 1
fi

if [[ -z "${AMENT_PREFIX_PATH:-}" ]]; then
  echo "AMENT_PREFIX_PATH is empty. Run inside 'nix develop' or 'direnv'." >&2
  exit 1
fi

cd "$HELPER_DIR"
colcon build --parallel-workers "$BUILD_JOBS" --event-handlers console_direct+
