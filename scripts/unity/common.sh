#!/usr/bin/env bash
# run_editmode.sh / run_playmode.sh から source される共通処理。
# 起動中の Unity Editor に uloop で接続できればそれを使い、
# できなければ Unity Editor を batchmode で起動してテストを実行する。

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT_PATH="${UNITY_PROJECT_PATH:-$ROOT_DIR/Ros2Unity}"
ARTIFACT_DIR="$ROOT_DIR/artifacts/unity"
PROJECT_VERSION_FILE="$PROJECT_PATH/ProjectSettings/ProjectVersion.txt"

HELP=0
FORCE_BATCH=0
FILTER_TYPE=""
FILTER_VALUE=""

usage() {
  local script_name="$1"
  local mode="$2"
  cat <<USAGE
Usage: scripts/unity/${script_name} [--batch] [--filter-type <exact|regex|assembly> --filter-value <value>]

${mode} テストを実行する。起動中の Unity Editor に uloop で接続できればそれを使い、
接続できなければ Unity Editor を batchmode で起動する。

Options:
  --batch                 batchmode 実行を強制する (Editor を閉じておくこと)
  --filter-type <type>    テストフィルタ種別: exact | regex | assembly
  --filter-value <value>  フィルタ値
  -h, --help              このヘルプを表示する

Environment:
  UNITY_EDITOR        batchmode 用 Unity 実行ファイルパス。未指定なら Unity Hub から自動検出。
  UNITY_PROJECT_PATH  プロジェクトパス。既定は ./Ros2Unity。
USAGE
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --batch)
        FORCE_BATCH=1
        shift
        ;;
      --filter-type)
        FILTER_TYPE="${2:?--filter-type には値が必要}"
        shift 2
        ;;
      --filter-value)
        FILTER_VALUE="${2:?--filter-value には値が必要}"
        shift 2
        ;;
      -h|--help)
        HELP=1
        return 0
        ;;
      *)
        echo "不明な引数: $1" >&2
        return 1
        ;;
    esac
  done

  if [[ -n "$FILTER_TYPE" && -z "$FILTER_VALUE" ]] || [[ -z "$FILTER_TYPE" && -n "$FILTER_VALUE" ]]; then
    echo "--filter-type と --filter-value は同時に指定すること" >&2
    return 1
  fi
  case "$FILTER_TYPE" in
    ""|exact|regex|assembly) ;;
    *)
      echo "不正な --filter-type: $FILTER_TYPE (exact | regex | assembly)" >&2
      return 1
      ;;
  esac
}

uloop_editor_available() {
  command -v uloop >/dev/null 2>&1 || return 1
  uloop execute-dynamic-code \
    --project-path "$PROJECT_PATH" \
    --code 'return "ok";' >/dev/null 2>&1
}

run_tests_uloop() {
  local mode="$1"
  local mode_lower
  mode_lower="$(echo "$mode" | tr '[:upper:]' '[:lower:]')"
  local json_file="$ARTIFACT_DIR/uloop-${mode_lower}-tests.json"

  local args=(run-tests --test-mode "$mode" --project-path "$PROJECT_PATH")
  if [[ -n "$FILTER_TYPE" ]]; then
    args+=(--filter-type "$FILTER_TYPE" --filter-value "$FILTER_VALUE")
  fi

  echo "uloop 経由で $mode テストを実行する (結果: $json_file)"
  if ! uloop "${args[@]}" | tee "$json_file"; then
    echo "uloop run-tests が失敗した" >&2
    return 1
  fi
  if ! grep -q '"Success": true' "$json_file" || ! grep -q '"FailedCount": 0' "$json_file"; then
    echo "テストが失敗した。$json_file を確認すること" >&2
    return 1
  fi
  if [[ -n "$FILTER_TYPE" ]] && grep -q '"TestCount": 0' "$json_file"; then
    echo "フィルタ '$FILTER_VALUE' にマッチするテストが 0 件だった。フィルタ値を確認すること" >&2
    return 1
  fi
}

detect_unity_editor() {
  if [[ -n "${UNITY_EDITOR:-}" ]]; then
    if [[ ! -x "$UNITY_EDITOR" ]]; then
      echo "UNITY_EDITOR が実行可能でない: $UNITY_EDITOR" >&2
      return 1
    fi
    return 0
  fi

  local version=""
  if [[ -f "$PROJECT_VERSION_FILE" ]]; then
    version="$(sed -n 's/^m_EditorVersion: //p' "$PROJECT_VERSION_FILE" | head -n 1)"
  fi
  if [[ -z "$version" ]]; then
    echo "ProjectVersion.txt から Unity バージョンを特定できない。UNITY_EDITOR を設定すること。" >&2
    return 1
  fi

  local candidates=(
    "/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity"
    "$HOME/Unity/Hub/Editor/$version/Editor/Unity"
  )
  local series="${version%.*}"
  local candidate
  for candidate in /Applications/Unity/Hub/Editor/"$series".*/Unity.app/Contents/MacOS/Unity; do
    candidates+=("$candidate")
  done
  for candidate in "$HOME"/Unity/Hub/Editor/"$series".*/Editor/Unity; do
    candidates+=("$candidate")
  done
  for candidate in "${candidates[@]}"; do
    if [[ -x "$candidate" ]]; then
      UNITY_EDITOR="$candidate"
      return 0
    fi
  done
  echo "Unity Editor $version (または同系列) が見つからない。UNITY_EDITOR を設定すること。" >&2
  return 1
}

run_tests_batch() {
  local mode="$1"
  local mode_lower
  mode_lower="$(echo "$mode" | tr '[:upper:]' '[:lower:]')"
  local results_file="$ARTIFACT_DIR/${mode_lower}-results.xml"
  local log_file="$ARTIFACT_DIR/unity-${mode_lower}.log"

  detect_unity_editor

  local args=(
    -batchmode
    -nographics
    -projectPath "$PROJECT_PATH"
    -runTests
    -testPlatform "$mode"
    -testResults "$results_file"
    -logFile "$log_file"
  )
  case "$FILTER_TYPE" in
    assembly) args+=(-assemblyNames "$FILTER_VALUE") ;;
    exact|regex) args+=(-testFilter "$FILTER_VALUE") ;;
  esac

  echo "batchmode で $mode テストを実行する (結果: $results_file)"
  "$UNITY_EDITOR" "${args[@]}"
}

run_unity_tests() {
  local mode="$1"
  mkdir -p "$ARTIFACT_DIR"
  if [[ "$FORCE_BATCH" == "1" ]]; then
    run_tests_batch "$mode"
  elif uloop_editor_available; then
    run_tests_uloop "$mode"
  else
    echo "uloop で Editor に接続できないため batchmode にフォールバックする"
    run_tests_batch "$mode"
  fi
}
