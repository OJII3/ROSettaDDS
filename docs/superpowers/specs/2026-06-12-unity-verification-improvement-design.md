# Unity 検証改善 設計

日付: 2026-06-12
状態: 承認済み

## 背景と課題

現状の Unity 検証 (`Ros2Unity`) には次のギャップがある。

- **実行基盤の不備**: `scripts/unity/run_unity_*_tests.sh` などの実行スクリプトは
  `fix: remove scripts` (0c474ac) で削除済みだが、`docs/unity-verification.md` は
  削除されたスクリプトを参照したままで、再現可能な実行手順が存在しない。
- **機能カバレッジ不足**: EditMode テストは `StringMessage` のみを使い、
  直近で実装された Reliable / Durability QoS や他の msg 型を Unity 上で検証していない。
- **Unity 固有の懸念が未検証**: コールバックスレッド、Domain Reload 無効時の挙動、
  IL2CPP/AOT 制約、長時間実行時の安定性が未検証。

## 基本方針

プロトコルロジックの詳細検証 (再送タイミング、QoS 互換性マトリクスなど) は
`.NET` テスト側 (`SedpReliableLoopbackTests`, `QosCompatibilityTests` など) の責務とする。
Unity 側の責務は次の 2 点に絞り、.NET テストと重複させない。

1. Unity ランタイム (Mono / IL2CPP) 上でも同じ振る舞いをすること
2. Unity のライフサイクル・スレッドモデルと安全に共存すること

## スコープ外

- GitHub Actions への Unity テスト組み込み (CI は引き続き .NET のみ)
- 外部 ROS 2 / Fast DDS との相互運用検証
- README への性能値自動反映 (`update_readme_performance.py` は廃止。
  計測結果は `artifacts/unity/` の XML を直接参照する運用とし、docs もそのように更新する。
  README に残っている古い性能ブロックは Phase 1 で削除する)

## フェーズ構成

各フェーズは独立した PR とし、その都度動作確認する。

### Phase 1: 実行基盤の再整備

- `scripts/unity/` に実行スクリプトを復活させる:
  - `run_editmode.sh` / `run_playmode.sh`
    - 起動中の Unity Editor があれば **uloop-cli 経由**で実行する
    - Editor 不在時は **batchmode にフォールバック**する
    - テストフィルタ (exact / regex / assembly) を引数で渡せるようにする
      (uloop の `run-tests` は NUnit カテゴリ非対応のため、Soak の分離は
      Phase 3 で専用テストアセンブリにより実現する)
  - 共通処理 (Editor 検出、`artifacts/unity/` への結果出力) は `common.sh` に集約
- `docs/unity-verification.md` を実態に同期する:
  - 削除済みスクリプトと `update_readme_performance.py` への参照を一掃
  - uloop 主軌 + batchmode 予備の実行手順を正とする

### Phase 2: EditMode カバレッジ拡充

`LoopbackHub` 上での QoS スモークと msg 型網羅を追加する。

- **QoS スモーク**:
  - Reliable: パケットロスを注入する `LossyTransport` デコレータ
    (`LoopbackTransport` ラッパー) を用意し、drop されても
    Heartbeat/AckNack 再送で全件到達すること
  - TransientLocal: publish 済みの履歴を late-join した reader が受信できること
  - BestEffort + ロス: 欠落してもハング・例外なく継続すること
- **msg 型網羅**:
  - 全生成型 (`std_msgs` / `builtin_interfaces`) の publish→receive roundtrip を
    パラメタライズドテスト化する
  - 型リストは IL2CPP (Player) 流用を見据えて**明示リスト**とする
  - 網羅漏れは「Msgs アセンブリ内の Serializer 実装数 == 明示リスト数」を
    reflection で照合する EditMode テスト (Mono 上なので reflection 可) で防ぐ
- **境界値**:
  - 空配列
  - 64 KiB 超の MultiArray (DataFrag 経路を通す)
  - UTF-8 マルチバイト文字列

### Phase 3: PlayMode / Unity 固有の挙動

- **コールバックスレッド検証**: subscription handler が呼ばれるスレッドを記録し、
  メインスレッドでないことを assert して仕様として明文化する。
  「handler 内で Unity API を直接呼べない」ことを docs にも記載する。
- **Domain Reload 無効**: Enter Play Mode Options で Domain Reload を無効にした状態で
  2 回連続 Play しても、static 残留状態が原因で失敗しないこと。
- **Play 停止時クリーンアップ**: Play 終了後に background receive スレッドが
  残っていないこと。UDP receive loop の内部診断カウンタが開始前の baseline に
  戻ることで直接確認する。
- **Soak テスト**: 専用テストアセンブリ (`ROSettaDDS.UnitySoak.Tests`) に分離し、
  通常実行から除外する (assembly フィルタで明示実行)。
  60 秒程度の連続 publish (50 Hz 目安) + 周期的 create/dispose を行い、
  エラーなし・retained memory 閾値内・受信継続を確認する。
- **フレーム影響**: publish 負荷中のフレーム時間スパイクを
  Unity Performance Testing の sample group に記録する
  (まず記録のみ。閾値は baseline 取得後に導入)。

### Phase 4: IL2CPP Player

- `scripts/unity/run_player_tests.sh`:
  Unity Test Runner の standalone 実行 (`-testPlatform StandaloneOSX`) を
  IL2CPP scripting backend で行い、ビルド成立と Player 上でのテスト実行を
  一度に検証する。
- **AOT 対応の棚卸し**:
  - `Publisher<T>` / `Subscription<T>` のジェネリクス AOT インスタンス化
  - ライブラリ内 reflection 使用箇所の確認
  - link.xml (コードストリッピング対策) の要否
- 切り分け用に Mono backend での Player 実行もオプションで残す。

## 判定方針

- Phase 2 の QoS / roundtrip テストは件数・内容一致・タイムアウトで失敗させる。
- Phase 3 の Soak は retained memory 閾値 (既存 leak guard と同じ
  managed 8 MiB / Unity mono 64 MiB を初期値) と受信継続で失敗させる。
- フレーム時間・throughput は引き続き記録のみとし、閾値導入は baseline 取得後。
