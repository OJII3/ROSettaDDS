# Unity 検証 Phase 3: PlayMode / Unity 固有挙動 実装計画

**Goal:** Unity PlayMode 上でコールバックスレッド、Domain Reload 無効時の再実行、
Play 停止時の受信ループ終了を検証し、長時間安定性とフレーム影響を明示実行の Soak
テストとして記録する。

**Architecture:** 通常 PlayMode テストは実 UDP と `MonoBehaviour` lifecycle を使い、
Unity 固有の契約を短時間で検証する。Soak は
`ROSettaDDS.UnitySoak.Tests` アセンブリへ分離し、通常の
`run_playmode.sh` から除外する。UDP receive loop の終了はテスト向け内部診断値で
直接確認する。

**Tech Stack:** C# / NUnit, Unity Test Framework 1.6.0,
Unity Performance Testing 3.2.0, ROSettaDDS UdpTransport

**Spec:** `docs/superpowers/specs/2026-06-12-unity-verification-improvement-design.md`

---

## Task 1: PlayMode のスレッド・lifecycle 検証を追加

- [ ] subscription handler の実行 thread ID を記録し、Unity main thread と異なることを確認する。
- [ ] `UdpTransport` に receive loop の内部診断カウンタを追加し、GameObject destroy 後に
  baseline へ戻ることを確認する。
- [ ] Domain Reload 無効の ProjectSettings で、同じ probe を 2 回連続して
  start / publish / stop できることを確認する。
- [ ] PlayMode アセンブリを実行して成功を確認する。

## Task 2: Soak テストを専用アセンブリへ分離

- [ ] `ROSettaDDS.UnitySoak.Tests` PlayMode アセンブリを追加する。
- [ ] 約 60 秒、50 Hz publish と周期的な participant / endpoint create-dispose を行う。
- [ ] 受信継続、例外なし、managed 8 MiB / Unity mono 64 MiB の retained memory
  閾値を確認する。
- [ ] publish 負荷中の frame time を Unity Performance Testing sample group に記録する。
- [ ] assembly フィルタを指定した明示実行で成功を確認する。

## Task 3: 実行基盤とドキュメントを同期

- [ ] `run_playmode.sh` の既定実行を通常 PlayMode アセンブリに限定する。
- [ ] Domain Reload 無効設定、callback thread 契約、Soak 明示実行方法、
  sample group を `docs/unity-verification.md` に記載する。
- [ ] Unity meta、.NET 回帰、通常 PlayMode、Soak を検証する。

