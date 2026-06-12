# Unity 検証 Phase 2: EditMode カバレッジ拡充 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Unity EditMode 上で Reliable / TransientLocal / BestEffort の QoS スモーク、全生成 msg 型の roundtrip、境界値を検証する。

**Architecture:** Phase 2 専用の EditMode テストを QoS と msg 型に分割し、共通の Loopback participant 構築とパケットロス注入はテスト支援ファイルへ集約する。msg 型は IL2CPP Player テストへ流用できる明示的なジェネリックケース一覧を正とし、Mono EditMode 上の reflection テストで Serializer 実装数との差分を検出する。

**Tech Stack:** C# / NUnit, Unity Test Framework 1.6.0, ROSettaDDS LoopbackTransport

**Spec:** `docs/superpowers/specs/2026-06-12-unity-verification-improvement-design.md`

---

## ファイル構成

- Create: `Ros2Unity/Assets/Tests/EditMode/UnityLoopbackTestSupport.cs`
  - Phase 2 テスト共通の participant pair、待機・発見確認、`LossyTransport`、RTPS submessage 判定を提供する。
- Create: `Ros2Unity/Assets/Tests/EditMode/ROSettaDDSUnityQosTests.cs`
  - Reliable 再送、TransientLocal late-join、BestEffort ロス継続の 3 スモークを担当する。
- Create: `Ros2Unity/Assets/Tests/EditMode/ROSettaDDSUnityGeneratedMessageTests.cs`
  - 全生成 msg 型の明示 roundtrip ケース、Serializer 網羅性、空配列・64 KiB 超・UTF-8 境界値を担当する。
- Create: 上記 3 ファイルに対応する `.meta`

### Task 1: Loopback / Lossy テスト支援を追加

- [ ] **Step 1: `UnityLoopbackTestSupport.cs` を追加する**

`UnityLoopbackTestSupport.CreatePair` は writer/reader 用の 8 transport を生成し、writer の user-unicast transport だけ任意デコレータで差し替えられるようにする。`LossyTransport` は `IRtpsTransport` を実装し、指定 predicate が true の送信を指定回数だけ正常終了扱いで破棄し、`DroppedCount` を記録する。

- [ ] **Step 2: RTPS submessage 判定を実装する**

RTPS header 後の submessage header を順に読み、little/big endian の長さを解釈して `SubmessageKind.Data` / `DataFrag` の存在を判定する。これにより discovery/HEARTBEAT/ACKNACK は落とさず DATA のみ確実に落とす。

- [ ] **Step 3: Unity コンパイルを確認する**

Run: `scripts/unity/run_editmode.sh --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests`

Expected: exit 0、既存 EditMode テストがすべて成功する。

- [ ] **Step 4: コミットする**

```bash
git add Ros2Unity/Assets/Tests/EditMode/UnityLoopbackTestSupport.cs*
git commit -m "test(unity): Loopback QoS 検証支援を追加"
```

### Task 2: QoS スモークテストを追加

- [ ] **Step 1: Reliable ロス再送テストを追加する**

writer user-unicast の最初の DATA を `LossyTransport` で破棄し、Reliable publisher/subscription が全メッセージを順序通り受信すること、`DroppedCount == 1` を確認する。

- [ ] **Step 2: Unity EditMode で Reliable ケースを確認する**

Run: `scripts/unity/run_editmode.sh --filter-type exact --filter-value ROSettaDDS.UnityVerification.Tests.ROSettaDDSUnityQosTests.Reliable_DATAをdropしても全件再送される`

Expected: exit 0。

- [ ] **Step 3: TransientLocal late-join テストを追加する**

Reliable + TransientLocal publisher で subscription 作成前に publish し、後発 Reliable subscription が履歴を受信することを確認する。

- [ ] **Step 4: BestEffort ロス継続テストを追加する**

BestEffort publisher/subscription で DATA を 2 件破棄し、欠落件数が期待通りで、その後の publish/receive が例外・ハングなく継続することを確認する。

- [ ] **Step 5: QoS クラス全体を確認する**

Run: `scripts/unity/run_editmode.sh --filter-type regex --filter-value ROSettaDDSUnityQosTests`

Expected: 3 ケースすべて成功する。

- [ ] **Step 6: コミットする**

```bash
git add Ros2Unity/Assets/Tests/EditMode/ROSettaDDSUnityQosTests.cs*
git commit -m "test(unity): QoS スモーク検証を追加"
```

### Task 3: 全生成 msg 型 roundtrip と網羅性検査を追加

- [ ] **Step 1: 明示 roundtrip ケース一覧を追加する**

`GeneratedMessageRoundTripCase<T>` に serializer とサンプル値を保持し、publish→receive 後に再シリアライズした CDR payload が送信前と一致することを確認する。`std_msgs` 30 型と `builtin_interfaces` 2 型をすべて明示列挙する。

- [ ] **Step 2: パラメタライズド roundtrip を確認する**

Run: `scripts/unity/run_editmode.sh --filter-type exact --filter-value ROSettaDDS.UnityVerification.Tests.ROSettaDDSUnityGeneratedMessageTests.全生成msg型をpublish_receiveできる`

Expected: 32 ケースすべて成功する。

- [ ] **Step 3: Serializer 網羅性 reflection テストを追加する**

Msgs アセンブリから `ICdrSerializer<>` 実装型を列挙し、明示一覧の serializer 型集合と完全一致することを確認する。新しい生成型が追加されて明示一覧が更新されない場合は失敗させる。

- [ ] **Step 4: 境界値テストを追加する**

- 空配列を持つ `ByteMultiArray` が空配列として roundtrip する。
- 64 KiB 超の `ByteMultiArray` が DATA_FRAG 経路で内容一致する。
- UTF-8 マルチバイト文字列が内容一致する。

- [ ] **Step 5: msg テストクラス全体を確認する**

Run: `scripts/unity/run_editmode.sh --filter-type regex --filter-value ROSettaDDSUnityGeneratedMessageTests`

Expected: 全ケース成功する。

- [ ] **Step 6: コミットする**

```bash
git add Ros2Unity/Assets/Tests/EditMode/ROSettaDDSUnityGeneratedMessageTests.cs*
git commit -m "test(unity): 全生成 msg 型と境界値の検証を追加"
```

### Task 4: Phase 2 全体検証

- [ ] **Step 1: Unity EditMode 全体を実行する**

Run: `scripts/unity/run_editmode.sh --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests`

Expected: exit 0、失敗 0。

- [ ] **Step 2: .NET 回帰テストを実行する**

Run: `dotnet test rosettadds.sln --no-restore`

Expected: exit 0、失敗 0。

- [ ] **Step 3: Unity meta を検査する**

Run: `.github/scripts/check_unity_meta.sh`

Expected: exit 0、不足・orphan なし。

- [ ] **Step 4: 差分と計画充足を確認する**

Run: `git status --short && git diff --check && git log --oneline main..HEAD`

Expected: 意図した Phase 2 ファイルのみ、whitespace error なし、段階コミットが存在する。
