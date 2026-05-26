#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT_PATH="${UNITY_PROJECT_PATH:-$ROOT_DIR/Ros2Unity}"
ARTIFACT_DIR="$ROOT_DIR/artifacts/unity"
UNITY_EDITOR="${UNITY_EDITOR:-}"
UNITY_USE_TEMP_PROJECT="${UNITY_USE_TEMP_PROJECT:-0}"
TMP_ROOT=""
PROJECT_VERSION_FILE="$PROJECT_PATH/ProjectSettings/ProjectVersion.txt"
PROJECT_EDITOR_VERSION=""

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  cat <<'USAGE'
Usage: scripts/unity/run_unity_editmode_tests.sh

Environment:
  UNITY_EDITOR        Unity executable path.
  UNITY_PROJECT_PATH  Project path. Defaults to ./Ros2Unity.
  UNITY_USE_TEMP_PROJECT
                      Set to 1 to run tests from a temporary project copy.
USAGE
  exit 0
fi

if [[ -z "$UNITY_EDITOR" ]]; then
  if [[ -f "$PROJECT_VERSION_FILE" ]]; then
    PROJECT_EDITOR_VERSION="$(sed -n 's/^m_EditorVersion: //p' "$PROJECT_VERSION_FILE" | head -n 1)"
  fi

  unity_editor_candidates=()
  if [[ -n "$PROJECT_EDITOR_VERSION" ]]; then
    unity_editor_candidates+=("/Applications/Unity/Hub/Editor/$PROJECT_EDITOR_VERSION/Unity.app/Contents/MacOS/Unity")
    project_editor_series="${PROJECT_EDITOR_VERSION%.*}"
    for candidate in /Applications/Unity/Hub/Editor/"$project_editor_series".*/Unity.app/Contents/MacOS/Unity; do
      unity_editor_candidates+=("$candidate")
    done
  fi

  for candidate in "${unity_editor_candidates[@]}"; do
    if [[ -x "$candidate" ]]; then
      UNITY_EDITOR="$candidate"
      break
    fi
  done
fi

if [[ -z "$UNITY_EDITOR" ]]; then
  if [[ -n "$PROJECT_EDITOR_VERSION" ]]; then
    echo "Unity Editor $PROJECT_EDITOR_VERSION or compatible series was not found. Set UNITY_EDITOR to the Unity executable path." >&2
  else
    echo "Unity project version was not found. Set UNITY_EDITOR to the Unity executable path." >&2
  fi
  exit 1
fi

if [[ ! -x "$UNITY_EDITOR" ]]; then
  echo "UNITY_EDITOR is not executable: $UNITY_EDITOR" >&2
  exit 1
fi

mkdir -p "$ARTIFACT_DIR"

if [[ "$UNITY_USE_TEMP_PROJECT" == "1" ]]; then
  TMP_ROOT="$(mktemp -d /tmp/rclsharp-unity-verify.XXXXXX)"
  mkdir -p "$TMP_ROOT/src"
  rsync -a --delete \
    --exclude Library \
    --exclude Temp \
    --exclude Logs \
    --exclude UserSettings \
    "$ROOT_DIR/Ros2Unity" "$TMP_ROOT/"
  rsync -a --delete \
    --exclude bin \
    --exclude obj \
    "$ROOT_DIR/src/rclsharp" "$TMP_ROOT/src/"
  PROJECT_PATH="$TMP_ROOT/Ros2Unity"
  trap 'rm -rf "$TMP_ROOT"' EXIT
fi

"$UNITY_EDITOR" \
  -batchmode \
  -nographics \
  -projectPath "$PROJECT_PATH" \
  -runTests \
  -testPlatform EditMode \
  -testResults "$ARTIFACT_DIR/editmode-results.xml" \
  -logFile "$ARTIFACT_DIR/unity-editmode.log"
