#!/usr/bin/env bash
set -euo pipefail

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

BACKEND="il2cpp"

player_usage() {
  cat <<USAGE
Usage: scripts/unity/run_player_tests.sh [--backend <il2cpp|mono>]

ROSettaDDS.UnityPlayer.Tests を StandaloneOSX Player で実行する。
既定 backend は IL2CPP。問題の切り分け時のみ Mono を指定する。

Options:
  --backend <backend>  scripting backend: il2cpp | mono
  -h, --help           このヘルプを表示する

Environment:
  UNITY_EDITOR        Unity 実行ファイルパス。未指定なら Unity Hub から自動検出。
  UNITY_PROJECT_PATH  プロジェクトパス。既定は ./Ros2Unity。
USAGE
}

parse_player_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --backend)
        BACKEND="${2:?--backend には値が必要}"
        shift 2
        ;;
      -h|--help)
        player_usage
        exit 0
        ;;
      *)
        echo "不明な引数: $1" >&2
        player_usage >&2
        return 1
        ;;
    esac
  done

  case "$BACKEND" in
    il2cpp|mono) ;;
    *)
      echo "不正な --backend: $BACKEND (il2cpp | mono)" >&2
      return 1
      ;;
  esac
}

run_player_tests() {
  detect_unity_editor
  mkdir -p "$ARTIFACT_DIR"

  local settings_file="$ROOT_DIR/scripts/unity/player-test-settings-$BACKEND.json"
  local build_dir="$ARTIFACT_DIR/player-$BACKEND"
  local results_file="$ARTIFACT_DIR/player-$BACKEND-results.xml"
  local log_file="$ARTIFACT_DIR/unity-player-$BACKEND.log"

  rm -rf "$build_dir"
  rm -f "$results_file" "$log_file"
  mkdir -p "$build_dir"

  echo "StandaloneOSX $BACKEND Player テストを実行する (結果: $results_file)"
  "$UNITY_EDITOR" \
    -batchmode \
    -nographics \
    -projectPath "$PROJECT_PATH" \
    -runTests \
    -testPlatform StandaloneOSX \
    -assemblyNames ROSettaDDS.UnityPlayer.Tests \
    -testSettingsFile "$settings_file" \
    -buildPlayerPath "$build_dir/ROSettaDDSUnityPlayerTests.app" \
    -testResults "$results_file" \
    -logFile "$log_file"

  if [[ ! -f "$results_file" ]]; then
    echo "Player テスト結果が生成されなかった。$log_file を確認すること" >&2
    return 1
  fi
  local test_run
  test_run="$(grep -m 1 '<test-run ' "$results_file" || true)"
  if [[ "$test_run" != *'result="Passed"'* \
     || "$test_run" != *'failed="0"'* \
     || "$test_run" == *'total="0"'* ]]; then
    echo "Player テストが失敗した。$results_file と $log_file を確認すること" >&2
    return 1
  fi
}

parse_player_args "$@"
run_player_tests
