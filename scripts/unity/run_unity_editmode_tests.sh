#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT_PATH="${UNITY_PROJECT_PATH:-$ROOT_DIR/Ros2Unity}"
ARTIFACT_DIR="$ROOT_DIR/artifacts/unity"
UNITY_EDITOR="${UNITY_EDITOR:-}"

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  cat <<'USAGE'
Usage: scripts/unity/run_unity_editmode_tests.sh

Environment:
  UNITY_EDITOR        Unity executable path.
  UNITY_PROJECT_PATH  Project path. Defaults to ./Ros2Unity.
USAGE
  exit 0
fi

if [[ -z "$UNITY_EDITOR" ]]; then
  for candidate in \
    "/Applications/Unity/Hub/Editor/6000.3.7f1/Unity.app/Contents/MacOS/Unity" \
    /Applications/Unity/Hub/Editor/*/Unity.app/Contents/MacOS/Unity; do
    if [[ -x "$candidate" ]]; then
      UNITY_EDITOR="$candidate"
      break
    fi
  done
fi

if [[ -z "$UNITY_EDITOR" || ! -x "$UNITY_EDITOR" ]]; then
  echo "Unity Editor executable was not found. Set UNITY_EDITOR to the Unity executable path." >&2
  exit 1
fi

mkdir -p "$ARTIFACT_DIR"

"$UNITY_EDITOR" \
  -batchmode \
  -nographics \
  -projectPath "$PROJECT_PATH" \
  -runTests \
  -testPlatform EditMode \
  -testResults "$ARTIFACT_DIR/editmode-results.xml" \
  -logFile "$ARTIFACT_DIR/unity-editmode.log" \
  -quit
