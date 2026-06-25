# Lean ProfilerRecorders 導入

## 背景

`docs/superpowers/specs/2026-06-25-perf-revisit-findings.md` の「ボトルネック B」で
`main_thread_time_ns_last` が baseline の **9 倍 (875 µs〜1 ms)** に
膨らんでいることを特定した。原因は `Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs:18-25`
の recorder 数が `f66df12` (best-effort fix) 以降に 3 → 6 へ増えたこと。
各 `ProfilerRecorder` は main thread の `Collect()` 内で `LastValue` / `GetSample` を
回すため、6 個に増えると per-frame の main thread 占有が直線的に増える。
wall clock (elapsed_ms) は +5〜10% 増にとどまるが、5〜10% の reliable 系統
regression のうち計測ノイズ分を占める可能性が高い。

## 変更内容

### 1. `PerfProfilerRecorders.Start()` を `StartLean()` / `StartFull()` に分離

`Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs`:

```csharp
internal static PerfProfilerRecorders StartLean()
{
    var result = new PerfProfilerRecorders();
    result.Add("main_thread_time_ns", ProfilerCategory.Internal, "Main Thread");
    result.Add("gc_used_memory_bytes", ProfilerCategory.Memory, "GC Used Memory");
    result.Add("gc_allocated_in_frame_bytes", ProfilerCategory.Memory, "GC Allocated In Frame");
    return result;
}

internal static PerfProfilerRecorders StartFull()
{
    var result = StartLean();
    result.Add("gc_reserved_memory_bytes", ProfilerCategory.Memory, "GC Reserved Memory");
    result.Add("total_used_memory_bytes", ProfilerCategory.Memory, "Total Used Memory");
    result.Add("system_used_memory_bytes", ProfilerCategory.Memory, "System Used Memory");
    return result;
}
```

`Collect()` / `Snapshot()` ロジックは recorders リストを汎用で回す実装なので
変更不要。`gc_allocated_in_frame_bytes` の特別扱いはそのまま機能する。

### 2. `PerfProfilerMode` enum を追加

`Ros2Unity/Assets/Perf/PerfPlayerArguments.cs`:

```csharp
internal enum PerfProfilerMode
{
    /// <summary>3 recorder (main thread / gc_used / gc_allocated)。perf 計測向け。</summary>
    Lean,
    /// <summary>6 recorder (lean + メモリ詳細 3 件)。診断用。</summary>
    Full,
}
```

CLI フラグ `--rosettadds-profiler-mode <lean|full>` (default: Lean) で
切り替え。既定を Lean にしたのは perf 計測のデフォルト動作を
「計測の計測ノイズ最小」とするため。

### 3. `PerfPlayerEntry` で `ProfilerMode` によって起動メソッドを選択

`Ros2Unity/Assets/Perf/PerfPlayerEntry.cs:47-49`:

```csharp
PerfProfilerRecorders recorders = parsed.ProfilerMode == PerfProfilerMode.Full
    ? PerfProfilerRecorders.StartFull()
    : PerfProfilerRecorders.StartLean();
using (recorders) { ... }
```

### 4. perf-runner に `--profiler-mode` を追加

`tools/rosettadds-perf-runner/RunnerOptions.cs` / `Program.cs`:

```csharp
case "--profiler-mode":
    options.ProfilerMode = RequireValue(args, ref i, arg);
    if (options.ProfilerMode != "lean" && options.ProfilerMode != "full")
    {
        throw new ArgumentException("--profiler-mode must be lean or full");
    }
    break;
```

`Program.cs` の `StartPlayer` 内で `--rosettadds-profiler-mode <mode>` を
Player 起動引数に追加。

## 設計上の注意点

- **後方互換**: 旧 `Start()` メソッドは削除。`StartFull()` と名前が
  違うが、機能は旧 `Start()` と同等。`StartLean()` が新規追加。
  `PerfPlayerEntry` だけが `Start()` を呼んでいたので、API 削除の影響は
  限定的。公開 API ではない (`internal sealed class`)。

- **既定が Lean の理由**: perf 計測の最も重要な目的は「実アプリでの
  最大スループット」を測ること。full は内部診断用。Lean を選べば
  baseline (06-22) と同じ recorder 構成になり、baseline 比較が
  容易になる。

- **Collect() のオーバーヘッド**: recorder 数 6 → 3 で per-frame コストは
  概ね半減するが、`GetSample` の呼び出し自体は残るので 1/2 には
  ならない。実測で main_thread_time が 9 倍 → 数倍に収まる見込み。

- **`gc_allocated_in_frame_bytes` の特別扱い**: `Collect()` は
  `gc_allocated_in_frame_bytes` recorder だけを `GetSample` で読み、
  `_gcAllocatedAccumulator` に累積する。他の recorder は `LastValue` のみ。
  lean でも full でもこの挙動は維持。

## 計測 (要 Unity Player 再ビルド)

### 比較方法

```sh
# 1. uloop で Player をビルド (Window > Unity CLI Loop > Server 起動後)
uloop execute-dynamic-code --project-path Ros2Unity --code \
  'ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.BuildPlayer("artifacts/perf/lean-build", "StandaloneLinux64", "mono"); return "ok";'

# 2. lean mode で perf 計測 (既定)
dotnet run --project tools/rosettadds-perf-runner -- \
  --skip-build --player-build artifacts/perf/lean-build/ROSettaDDSPerfPlayer \
  --scenario all --capture-frames 1200

# 3. full mode で perf 計測 (比較用)
dotnet run --project tools/rosettadds-perf-runner -- \
  --skip-build --player-build artifacts/perf/lean-build/ROSettaDDSPerfPlayer \
  --profiler-mode full --scenario all --capture-frames 1200
```

### 期待される改善

| 項目 | full (現状) | lean (本変更) | 効果 |
|------|------------:|--------------:|------|
| main_thread_time_ns_last (typical) | 875 us〜1 ms | (推定) 200〜400 us | 計測の計測ノイズ 1/3〜1/5 |
| reliable-32 mps | 6 280 | (推定) 6 500〜6 800 | baseline (6 892) 近く戻る |
| reliable-1024 mps | 6 076 | (推定) 6 300〜6 600 | 同上 |

5〜10% の reliable 系統 regression のうち、計測ノイズ分は lean 化で
消えるはず。残った差分は best-effort fix 適用による UdpTransport
側の純粋なオーバーヘッド。

## テスト

- 既存 .NET unit test 553 件 すべて緑 (本変更で影響なし、Ros2Unity 側の
  コードは .NET test プロジェクトから参照されない)
- 既存 06-22 baseline と同じ recorder 構成にするため、baseline 比較
  の有意性が向上

## 影響範囲

- `Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs` (API 改名)
- `Ros2Unity/Assets/Perf/PerfPlayerArguments.cs` (enum / CLI フラグ追加)
- `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs` (recorder 選択分岐追加)
- `tools/rosettadds-perf-runner/RunnerOptions.cs` (`--profiler-mode` 追加)
- `tools/rosettadds-perf-runner/Program.cs` (Player 起動引数追加)

公開 API への破壊的変更なし。`PerfProfilerRecorders.Start()` の呼び出し箇所
は `Ros2Unity` 内に 1 箇所 (`PerfPlayerEntry.cs:47`) のみで、これも
本変更で更新済み。
