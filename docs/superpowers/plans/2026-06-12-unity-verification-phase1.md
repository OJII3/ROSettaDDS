# Unity 検証 Phase 1: 実行基盤の再整備 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 削除された Unity テスト実行スクリプトを uloop 主軌 + batchmode 予備で再整備し、docs と README を実態に同期する。

**Architecture:** `scripts/unity/common.sh` に Editor 検出・uloop 接続判定・実行ロジックを集約し、`run_editmode.sh` / `run_playmode.sh` は mode を固定する薄いラッパーにする。uloop が起動中 Editor に接続できればそれを使い、できなければ batchmode にフォールバックする。

**Tech Stack:** bash, uloop-cli 2.1.6 (uLoopMCP), Unity 6000.3 Test Runner

**Spec:** `docs/superpowers/specs/2026-06-12-unity-verification-improvement-design.md`

---

## 前提知識

- uloop-cli は nix devshell に入っており `uloop` で起動できる。Unity Editor 側に
  uLoopMCP パッケージ (`io.github.hatayama.uloopmcp`) が導入済みで、Editor が起動中なら
  `uloop get-version --project-path <path>` が JSON を返す (接続不可なら非 0 で終了)。
- `uloop run-tests` の出力は JSON 一発 (`Success`, `TestCount`, `PassedCount`,
  `FailedCount`, `XmlPath` など)。**`XmlPath` は null になる**ため、Unity Performance
  Testing の sample group XML が必要なときは batchmode で実行する。
- `uloop run-tests` のフィルタは `--filter-type <all|exact|regex|assembly>` +
  `--filter-value`。NUnit カテゴリは指定できない。
- batchmode は同じプロジェクトを Editor で開いていると起動できない (Unity の制約)。
- テストアセンブリ名: EditMode = `ROSettaDDS.UnityVerification.Tests`、
  PlayMode = `ROSettaDDS.UnityPlayMode.Tests`。
- 旧スクリプト (削除済み) は `git show 0c474ac~1:scripts/unity/run_unity_editmode_tests.sh`
  で参照できる。旧スクリプトの `src/rclsharp` パスはリネーム前のもので、現在は
  `src/rosettadds`。
- 作業ブランチ: `docs/unity-verification-improvement` をそのまま使う。

---

### Task 1: scripts/unity/common.sh を作成

**Files:**
- Create: `scripts/unity/common.sh`

- [ ] **Step 1: common.sh を書く**

以下の内容で `scripts/unity/common.sh` を作成する:

```bash
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
  uloop get-version --project-path "$PROJECT_PATH" >/dev/null 2>&1
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

  local candidates=("/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity")
  local series="${version%.*}"
  local candidate
  for candidate in /Applications/Unity/Hub/Editor/"$series".*/Unity.app/Contents/MacOS/Unity; do
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
```

- [ ] **Step 2: 構文チェック**

Run: `bash -n scripts/unity/common.sh`
Expected: 出力なし (exit 0)

- [ ] **Step 3: コミット**

```bash
git add scripts/unity/common.sh
git commit -m "feat(unity): テスト実行スクリプトの共通処理を追加"
```

---

### Task 2: run_editmode.sh を作成

**Files:**
- Create: `scripts/unity/run_editmode.sh`

- [ ] **Step 1: run_editmode.sh を書く**

以下の内容で `scripts/unity/run_editmode.sh` を作成する:

```bash
#!/usr/bin/env bash
set -euo pipefail

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

if ! parse_args "$@"; then
  usage "run_editmode.sh" "EditMode" >&2
  exit 1
fi
if [[ "$HELP" == "1" ]]; then
  usage "run_editmode.sh" "EditMode"
  exit 0
fi

run_unity_tests EditMode
```

- [ ] **Step 2: 実行権限を付与して構文チェック**

Run: `chmod +x scripts/unity/run_editmode.sh && bash -n scripts/unity/run_editmode.sh`
Expected: 出力なし (exit 0)

- [ ] **Step 3: ヘルプ表示を確認**

Run: `scripts/unity/run_editmode.sh --help`
Expected: `Usage: scripts/unity/run_editmode.sh ...` で始まるヘルプが表示され exit 0

- [ ] **Step 4: 不正引数でエラーになることを確認**

Run: `scripts/unity/run_editmode.sh --filter-type exact; echo "exit=$?"`
Expected: `--filter-type と --filter-value は同時に指定すること` と Usage が stderr に出て `exit=1`

- [ ] **Step 5: コミット**

```bash
git add scripts/unity/run_editmode.sh
git commit -m "feat(unity): EditMode テスト実行スクリプトを追加"
```

---

### Task 3: run_playmode.sh を作成

**Files:**
- Create: `scripts/unity/run_playmode.sh`

- [ ] **Step 1: run_playmode.sh を書く**

以下の内容で `scripts/unity/run_playmode.sh` を作成する (run_editmode.sh の mode 違い):

```bash
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
```

- [ ] **Step 2: 実行権限を付与して構文チェック**

Run: `chmod +x scripts/unity/run_playmode.sh && bash -n scripts/unity/run_playmode.sh`
Expected: 出力なし (exit 0)

- [ ] **Step 3: コミット**

```bash
git add scripts/unity/run_playmode.sh
git commit -m "feat(unity): PlayMode テスト実行スクリプトを追加"
```

---

### Task 4: uloop 経由での実行を検証

**Files:** なし (検証のみ)

前提: Unity Editor で `Ros2Unity` プロジェクトが開いていること
(`uloop get-version --project-path ./Ros2Unity` が JSON を返すことで確認できる)。
Editor が起動していない場合はこの Task をスキップし、Task 5 の batchmode 検証を先に行う。

- [ ] **Step 1: フィルタ付きで smoke テスト 1 件を実行**

Run:
```bash
scripts/unity/run_editmode.sh --filter-type regex --filter-value 'Loopback_pubsub_で_StringMessage'
```
Expected: `uloop 経由で EditMode テストを実行する` のあとに JSON が出力され、
`"Success": true`, `"FailedCount": 0`, `"TestCount": 1` を含み exit 0。
`artifacts/unity/uloop-editmode-tests.json` に同じ JSON が保存されている。

- [ ] **Step 2: EditMode 全件を実行**

Run: `scripts/unity/run_editmode.sh`
Expected: 数分かかる (throughput / leak guard を含む)。`"Success": true`,
`"FailedCount": 0` で exit 0。

- [ ] **Step 3: PlayMode 全件を実行**

Run: `scripts/unity/run_playmode.sh`
Expected: `"Success": true`, `"FailedCount": 0` で exit 0。
`artifacts/unity/uloop-playmode-tests.json` が生成されている。

- [ ] **Step 4: テスト失敗時に exit code が非 0 になることを確認**

存在しないテスト名の exact フィルタでは TestCount 0 のまま成功扱いになる可能性があるため、
出力 JSON の内容を確認する:

Run:
```bash
scripts/unity/run_editmode.sh --filter-type exact --filter-value 'NonExistent.Test'; echo "exit=$?"
```
Expected: `"Success": false` または `"TestCount": 0` の JSON が出力される。
`"Success": false` の場合はスクリプトが `テストが失敗した` を出して `exit=1` になること。
`"Success": true` かつ `"TestCount": 0` で exit 0 になる場合は「フィルタが 1 件もマッチ
しなければ気付けない」ことを意味するので、`run_tests_uloop` に
`grep -q '"TestCount": 0' && フィルタ指定時はエラー` の処理を追加して再実行する。

- [ ] **Step 5: 修正があればコミット**

```bash
git add scripts/unity/common.sh
git commit -m "fix(unity): uloop 実行で 0 件マッチを失敗として扱う"
```
(Step 4 で修正が不要だった場合はスキップ)

---

### Task 5: batchmode フォールバックを検証

**Files:** なし (検証のみ)

batchmode は `Ros2Unity` を Editor で開いていると起動できない。
**ユーザーに Editor を閉じてよいか確認してから実行する**こと。閉じられない場合は
この Task を保留とし、PR 前にユーザーへ手動確認を依頼する。

- [ ] **Step 1: Editor を閉じた状態で batchmode 実行**

Run:
```bash
scripts/unity/run_editmode.sh --batch --filter-type regex --filter-value 'Loopback_pubsub_で_StringMessage'
```
Expected: `batchmode で EditMode テストを実行する` と出力され、数分後に exit 0。
`artifacts/unity/editmode-results.xml` と `artifacts/unity/unity-editmode.log` が
生成されている。

- [ ] **Step 2: フォールバック動作の確認**

Editor を閉じたまま `--batch` なしで実行する:

Run:
```bash
scripts/unity/run_editmode.sh --filter-type regex --filter-value 'Loopback_pubsub_で_StringMessage'
```
Expected: `uloop で Editor に接続できないため batchmode にフォールバックする` と出力され、
batchmode で実行されて exit 0。

---

### Task 6: README の古い性能ブロックと不要ファイルを削除

**Files:**
- Modify: `README.md:234-258`
- Delete: `scripts/unity/__pycache__/` (untracked の残骸)

- [ ] **Step 1: README の性能ブロックを削除**

`README.md` の以下のブロック全体 (前後の空行調整を含む) を削除する:

```markdown
<!-- rosettadds-local-performance:start -->
## ローカル性能計測結果
...(中略: Throughput / Leak Guard のテーブル)...
<!-- rosettadds-local-performance:end -->
```

削除後、README 末尾が `SpdpDemo 側のログに ... が出れば OK。` の段落で終わることを確認する。

- [ ] **Step 2: __pycache__ を削除**

Run: `rm -rf scripts/unity/__pycache__`
(untracked なので git 操作は不要)

- [ ] **Step 3: コミット**

```bash
git add README.md
git commit -m "docs: README の古いローカル性能計測ブロックを削除"
```

---

### Task 7: docs/unity-verification.md を実態に同期

**Files:**
- Modify: `docs/unity-verification.md`

- [ ] **Step 1: 「実行方法」セクションを差し替える**

`## 実行方法` から `## 判定方針` の直前までを、以下の内容に置き換える:

````markdown
## 実行方法

実行スクリプトは、起動中の Unity Editor に uloop (uLoopMCP) で接続できればそれを使い、
接続できなければ Unity Editor を batchmode で起動する。

```sh
scripts/unity/run_editmode.sh
scripts/unity/run_playmode.sh
```

batchmode を強制する場合 (`Ros2Unity` を開いている Editor は閉じておくこと):

```sh
scripts/unity/run_editmode.sh --batch
```

特定のテストだけ実行する場合:

```sh
scripts/unity/run_editmode.sh --filter-type regex --filter-value 'Loopback_pubsub'
scripts/unity/run_playmode.sh --filter-type assembly --filter-value ROSettaDDS.UnityPlayMode.Tests
```

batchmode 用の Unity Editor は `ProjectSettings/ProjectVersion.txt` のバージョンを基に
Unity Hub の標準パスから自動検出する。明示する場合:

```sh
UNITY_EDITOR=/Applications/Unity/Hub/Editor/6000.3.7f1/Unity.app/Contents/MacOS/Unity \
  scripts/unity/run_editmode.sh --batch
```

出力先:

- uloop 実行: `artifacts/unity/uloop-editmode-tests.json` / `artifacts/unity/uloop-playmode-tests.json`
  (テスト件数と pass/fail のサマリ JSON)
- batchmode 実行: `artifacts/unity/editmode-results.xml` + `artifacts/unity/unity-editmode.log` /
  `artifacts/unity/playmode-results.xml` + `artifacts/unity/unity-playmode.log`

`artifacts/` は計測結果の生成物なのでコミットしない。

Unity Performance Testing の sample group (throughput / leak guard の計測値) は
batchmode 実行で生成される results XML にのみ埋め込まれる。性能値を確認するときは
`--batch` で実行し、XML を直接参照する。README への性能値の自動反映は行わない。
````

- [ ] **Step 2: 「目的」セクションの README 反映に関する記述を更新**

冒頭の目的・検証範囲に古い記述が残っていないか確認する。具体的には:

- `通信処理のバッチ時間、...を EditMode テストで記録する。` はそのまま残してよい
- README 反映に言及する箇所があれば削除する

- [ ] **Step 3: 変更内容をレビュー**

Run: `git diff docs/unity-verification.md`
Expected: 削除済みスクリプト (`run_unity_editmode_tests.sh`, `update_readme_performance.py`,
`UNITY_USE_TEMP_PROJECT`) への言及がすべて消えていること。

- [ ] **Step 4: コミット**

```bash
git add docs/unity-verification.md
git commit -m "docs: Unity 検証手順を uloop 主軌の実行スクリプトに同期"
```

---

### Task 8: 最終確認と PR 作成

**Files:** なし

- [ ] **Step 1: 全スクリプトの最終確認**

Run:
```bash
bash -n scripts/unity/common.sh scripts/unity/run_editmode.sh scripts/unity/run_playmode.sh
git grep -l "update_readme_performance\|run_unity_editmode_tests\|UNITY_USE_TEMP_PROJECT" -- docs/unity-verification.md README.md || echo "stale 参照なし"
```
Expected: 構文エラーなし、`stale 参照なし` (docs/superpowers/ 配下の spec・plan 内の言及は対象外)

- [ ] **Step 2: push して PR を作成**

```bash
git push -u origin docs/unity-verification-improvement
gh pr create --title "feat: Unity テスト実行基盤の再整備 (Phase 1)" --body "..."
```

PR body には spec へのリンク、uloop 主軌 + batchmode 予備の構成、検証結果
(Task 4/5 の実行ログ要約) を記載する。

---

## Self-Review 済みの注意点

- uloop の `run-tests` は結果 XML を返さない (`XmlPath: null`)。性能計測の運用は
  batchmode 実行が前提になることを docs に明記する (Task 7 でカバー)。
- Task 4 Step 4 の「0 件マッチ」挙動は実測しないと分からないため、確認結果に応じて
  `run_tests_uloop` を修正する分岐を計画に含めてある。
- batchmode 検証 (Task 5) はユーザーの Editor を閉じる必要があるため、実行前に必ず
  ユーザーに確認する。
