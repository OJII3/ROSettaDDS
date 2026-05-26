#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT_PATH="${UNITY_PROJECT_PATH:-$ROOT_DIR/Ros2Unity}"

if [[ -n "${UNITY_MCP_RELAY:-}" ]]; then
  RELAY="$UNITY_MCP_RELAY"
else
  case "$(uname -s):$(uname -m)" in
    Darwin:arm64)
      RELAY="$HOME/.unity/relay/relay_mac_arm64.app/Contents/MacOS/relay_mac_arm64"
      ;;
    Darwin:x86_64)
      RELAY="$HOME/.unity/relay/relay_mac_x64.app/Contents/MacOS/relay_mac_x64"
      ;;
    Linux:*)
      RELAY="$HOME/.unity/relay/relay_linux"
      ;;
    *)
      echo "Unsupported Unity MCP relay platform: $(uname -s) $(uname -m)" >&2
      exit 1
      ;;
  esac
fi

if [[ ! -x "$RELAY" ]]; then
  echo "Unity MCP relay was not found or is not executable: $RELAY" >&2
  echo "Open Ros2Unity in Unity once and confirm Edit > Project Settings > AI > Unity MCP Server is running." >&2
  exit 1
fi

exec "$RELAY" --mcp --project-path "$PROJECT_PATH" "$@"
