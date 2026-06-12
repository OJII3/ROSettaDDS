# Unity 検証 Phase 4: IL2CPP Player 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Standalone macOS Player を IL2CPP backend でビルド・実行し、ROSettaDDS の全生成 msg 型と `Publisher<T>` / `Subscription<T>` が AOT 環境で動作することを検証する。

**Architecture:** reflection を含む EditMode テストとは別に、Player 専用 PlayMode テストアセンブリを追加する。Player テストは全 32 msg 型をジェネリックメソッドへ明示的に渡して AOT インスタンス化し、Test Framework の TestSettings ファイルで IL2CPP / Mono backend を切り替える。ROSettaDDS 本体は preserve せず、NUnit の reflection discovery に必要な Player テストアセンブリだけを `link.xml` で preserve する。

**Tech Stack:** Bash, C# / NUnit, Unity Test Framework 1.6.0, Unity 6000.3 StandaloneOSX Player, Mono / IL2CPP

**Spec:** `docs/superpowers/specs/2026-06-12-unity-verification-improvement-design.md`

---

## ファイル構成

- Create: `Ros2Unity/Assets/Tests/Player/ROSettaDDS.UnityPlayer.Tests.asmdef`
  - Standalone Player に含める Phase 4 専用テストアセンブリ。
- Create: `Ros2Unity/Assets/Tests/Player/ROSettaDDSUnityAotPlayerTests.cs`
  - 全生成 msg 型の serializer と `Publisher<T>` / `Subscription<T>` を明示参照し、AOT Player 上の publish/receive を検証する。
- Create: `Ros2Unity/Assets/Tests/Player/link.xml`
  - IL2CPP Player で NUnit が Player テストアセンブリを reflection discovery できるよう preserve する。
- Create: `scripts/unity/player-test-settings-il2cpp.json`
  - `scriptingBackend: IL2CPP` と macOS architecture を指定する。
- Create: `scripts/unity/player-test-settings-mono.json`
  - 切り分け用に `scriptingBackend: Mono2x` と macOS architecture を指定する。
- Create: `scripts/unity/run_player_tests.sh`
  - StandaloneOSX Player テストを既定 IL2CPP、`--backend mono` で Mono 実行する。
- Modify: `docs/unity-verification.md`
  - Player 実行方法、AOT / reflection / stripping 棚卸し結果、成果物を記載する。

### Task 1: Player 専用 AOT スモークを追加

- [x] **Step 1: Player テスト asmdef と空のテストクラスを追加する**

`ROSettaDDS.UnityPlayer.Tests` は `ROSettaDDS` と `UnityEngine.TestRunner` を参照し、
`UNITY_INCLUDE_TESTS` でのみコンパイルする。Editor 専用 API と reflection は参照しない。

- [x] **Step 2: Player アセンブリ指定で RED を確認する**

Run:
`scripts/unity/run_playmode.sh --filter-type assembly --filter-value ROSettaDDS.UnityPlayer.Tests`

Expected: Player 用の AOT スモークがまだ存在しないため、対象テスト 0 件として失敗する。

- [x] **Step 3: 全 32 msg 型の AOT スモークを追加する**

各型について `AssertRoundTrip<T>(ICdrSerializer<T>, T)` を明示呼び出しし、同一プロセスの
`LoopbackHub` を使って `CreatePublisher<T>` / `CreateSubscription<T>`、
publish、受信、CDR payload 一致を確認する。動的型生成と serializer reflection は使わない。

- [x] **Step 4: Player テストを Editor PlayMode で GREEN にする**

Run:
`scripts/unity/run_playmode.sh --filter-type assembly --filter-value ROSettaDDS.UnityPlayer.Tests`

Expected: exit 0、Player AOT スモーク成功。

- [x] **Step 5: コミットする**

```bash
git add Ros2Unity/Assets/Tests/Player*
git commit -m "test(unity): Player AOT スモークを追加"
```

### Task 2: Standalone Player 実行スクリプトを追加

- [x] **Step 1: スクリプト契約の RED を確認する**

Run: `scripts/unity/run_player_tests.sh --help`

Expected: ファイル未作成のため失敗する。

- [x] **Step 2: backend 切り替え用 TestSettings を追加する**

IL2CPP 設定は `{"scriptingBackend":"IL2CPP","architecture":"arm64"}`、
Mono 設定は `{"scriptingBackend":"Mono2x","architecture":"arm64"}` とする。

- [x] **Step 3: `run_player_tests.sh` を追加する**

`--backend il2cpp|mono` を解析し、Unity Editor を検出後、次を実行する。

```bash
"$UNITY_EDITOR" \
  -batchmode \
  -nographics \
  -projectPath "$PROJECT_PATH" \
  -runTests \
  -testPlatform StandaloneOSX \
  -assemblyNames ROSettaDDS.UnityPlayer.Tests \
  -testSettingsFile "$settings_file" \
  -buildPlayerPath "$ARTIFACT_DIR/player-$backend/ROSettaDDSUnityPlayerTests.app" \
  -testResults "$ARTIFACT_DIR/player-$backend-results.xml" \
  -logFile "$ARTIFACT_DIR/unity-player-$backend.log"
```

引数不正時は非 0、`--help` は 0 で終了する。Player 実行は Editor 接続状態に依存しない
batchmode 専用とする。

- [x] **Step 4: shell 契約を GREEN にする**

Run:

```bash
scripts/unity/run_player_tests.sh --help
scripts/unity/run_player_tests.sh --backend invalid
bash -n scripts/unity/run_player_tests.sh scripts/unity/common.sh
```

Expected: help は exit 0、不正 backend は非 0、`bash -n` は exit 0。

- [x] **Step 5: Mono Player を実行する**

Run: `scripts/unity/run_player_tests.sh --backend mono`

Expected: StandaloneOSX Mono Player のビルドと Player AOT スモークが成功する。

- [x] **Step 6: IL2CPP Player を実行する**

Run: `scripts/unity/run_player_tests.sh`

Expected: StandaloneOSX IL2CPP Player のビルドと Player AOT スモークが成功する。

- [x] **Step 7: コミットする**

```bash
git add scripts/unity/run_player_tests.sh scripts/unity/player-test-settings-*.json
git commit -m "test(unity): Standalone Player 実行基盤を追加"
```

### Task 3: AOT 棚卸しと全体検証

- [x] **Step 1: AOT / reflection / stripping 棚卸しを docs に記録する**

記録内容:

- 全 32 msg 型は Player テストの `AssertRoundTrip<T>` 明示呼び出しにより、
  serializer、`Publisher<T>`、`Subscription<T>` を AOT インスタンス化する。
- ライブラリ内 reflection は `DomainParticipant.ResolveDdsTypeName<T>` の
  `DdsTypeName` 定数取得のみで、Player roundtrip により実行確認する。
- ROSettaDDS 本体を preserve する `link.xml` は不要。
- NUnit の reflection discovery には Player テストアセンブリを preserve する
  `Assets/Tests/Player/link.xml` が必要。

- [x] **Step 2: ドキュメントをコミットする**

```bash
git add docs/unity-verification.md docs/superpowers/plans/2026-06-13-unity-verification-phase4.md
git commit -m "docs(unity): IL2CPP Player 検証手順を追加"
```

- [ ] **Step 3: 回帰検証を実行する**

Run:

```bash
scripts/unity/run_editmode.sh --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests
scripts/unity/run_playmode.sh --filter-type assembly --filter-value ROSettaDDS.UnityPlayMode.Tests
scripts/unity/run_player_tests.sh --backend mono
scripts/unity/run_player_tests.sh
dotnet test rosettadds.sln --no-restore
.github/scripts/check_unity_meta.sh
git diff --check
```

Expected: すべて exit 0、Unity / .NET テスト失敗 0、Unity package meta の不足・orphan なし。

- [ ] **Step 4: 差分と成果物を確認する**

Run:
`git status --short && git log --oneline main..HEAD && find artifacts/unity -maxdepth 2 -type f | sort`

Expected: Phase 4 の意図したファイルのみが差分となり、Mono / IL2CPP Player の results XML、
ログ、Player build が `artifacts/unity/` に存在する。
