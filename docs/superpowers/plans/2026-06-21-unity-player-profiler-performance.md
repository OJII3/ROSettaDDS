# Unity Player Profiler Performance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ROS 2 devShell 内の外部 runner から Unity Development Player と ROS 2 helper を起動し、Profiler capture と metrics を再現可能な artifact として保存する。

**Architecture:** Unity 側は runtime Player harness と Editor build method に分ける。ROS 2 側 helper は endpoint process として残し、.NET runner が helper / Player の起動、同期、artifact manifest 生成を担当する。旧 Unity PlayMode perf test は Unity が ROS 2 helper を起動する構造なので削除する。

**Tech Stack:** Unity 6000.3, C# runtime scripts, UnityEditor BuildPipeline, .NET 8 console tool, ROS 2 Humble helper, Unity Profiler command line arguments

---

## File Structure

- Delete: `Ros2Unity/Assets/Tests/Ros2Perf/`
  - 旧 PlayMode perf test。Unity 側から ROS 2 helper を起動するため削除する。
- Create: `Ros2Unity/Assets/Perf/ROSettaDDS.UnityPerfHarness.asmdef`
  - Player runtime harness assembly。
- Create: `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs`
  - `RuntimeInitializeOnLoadMethod` で perf mode を検出し、scenario を実行する。
- Create: `Ros2Unity/Assets/Perf/PerfPlayerArguments.cs`
  - Player 独自引数 parser。
- Create: `Ros2Unity/Assets/Perf/PerfMetricsWriter.cs`
  - JSON Lines metrics / sentinel file writer。
- Create: `Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs`
  - ProfilerRecorder counter の開始、停止、集計。
- Create: `Ros2Unity/Assets/Editor/ROSettaDDSPerfPlayerBuilder.cs`
  - Unity Editor batchmode から専用 Development Player を build する。
- Create: `tools/rosettadds-perf-runner/rosettadds-perf-runner.csproj`
  - .NET 8 console runner。
- Create: `tools/rosettadds-perf-runner/Program.cs`
  - argument parsing と orchestration entrypoint。
- Create: `tools/rosettadds-perf-runner/*.cs`
  - scenario, process runner, event parsing, artifact manifest。
- Modify: `docs/unity-verification.md`
  - 新 workflow を記載し、旧 PlayMode perf 手順を削除する。
- Modify: `docs/interop.md`
  - Unity が helper を起動する説明を削除し、external runner 方式に更新する。

All files under `Ros2Unity/Assets` need corresponding `.meta` files.

---

### Task 1: Remove obsolete Unity PlayMode perf harness

**Files:**
- Delete: `Ros2Unity/Assets/Tests/Ros2Perf/`
- Modify: `docs/unity-verification.md`
- Modify: `docs/interop.md`

- [ ] **Step 1: Delete old Ros2Perf Unity assets**

Remove the whole `Ros2Unity/Assets/Tests/Ros2Perf/` directory and `Ros2Unity/Assets/Tests/Ros2Perf.meta`.

- [ ] **Step 2: Search stale references**

Run:

```bash
rg -n "UnityRos2Perf|ROSettaDDS.UnityRos2Perf|Ros2Perf|ROSETTADDS_ROS2_PERF_HELPER" Ros2Unity docs scripts
```

Expected: references remain only where documenting removed history is intentional. In final implementation, no active Unity workflow references `ROSettaDDS.UnityRos2Perf.Tests`.

- [ ] **Step 3: Commit**

```bash
git add Ros2Unity/Assets/Tests/Ros2Perf Ros2Unity/Assets/Tests/Ros2Perf.meta docs/unity-verification.md docs/interop.md
git commit -m "refactor: 旧 Unity ROS 2 perf テストを削除"
```

---

### Task 2: Add Unity Player runtime harness

**Files:**
- Create: `Ros2Unity/Assets/Perf.meta`
- Create: `Ros2Unity/Assets/Perf/ROSettaDDS.UnityPerfHarness.asmdef`
- Create: `Ros2Unity/Assets/Perf/ROSettaDDS.UnityPerfHarness.asmdef.meta`
- Create: `Ros2Unity/Assets/Perf/PerfPlayerArguments.cs`
- Create: `Ros2Unity/Assets/Perf/PerfPlayerArguments.cs.meta`
- Create: `Ros2Unity/Assets/Perf/PerfMetricsWriter.cs`
- Create: `Ros2Unity/Assets/Perf/PerfMetricsWriter.cs.meta`
- Create: `Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs`
- Create: `Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs.meta`
- Create: `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs`
- Create: `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs.meta`

- [ ] **Step 1: Add argument parser**

Implement a parser that accepts `--rosettadds-perf`, `--scenario`, `--direction`, `--domain-id`, `--topic`, `--qos`, `--payload-bytes`, `--messages`, `--ready-file`, `--done-file`, and `--metrics-file`. Invalid input returns an error string and exits through the metrics writer.

- [ ] **Step 2: Add metrics writer**

Write compact JSON Lines with event name, scenario fields, numeric values, and error messages. Create sentinel files atomically enough for local process synchronization by writing a short UTF-8 text file.

- [ ] **Step 3: Add ProfilerRecorder wrapper**

Start available counters only. Record unavailable counters with `available=false`; do not add fallback counters.

- [ ] **Step 4: Add runtime entrypoint**

`RuntimeInitializeOnLoadMethod(BeforeSceneLoad)` checks for `--rosettadds-perf`. If absent, it returns immediately. If present, it runs the selected scenario and calls `Application.Quit(0)` or `Application.Quit(1)`.

- [ ] **Step 5: Verify C# compile through Unity EditMode**

Run:

```bash
scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests
```

Expected: Unity compiles project and tests pass or fail only for pre-existing unrelated runtime issues.

- [ ] **Step 6: Commit**

```bash
git add Ros2Unity/Assets/Perf Ros2Unity/Assets/Perf.meta
git commit -m "feat: Unity Player 性能計測 harness を追加"
```

---

### Task 3: Add Unity Editor build method

**Files:**
- Create: `Ros2Unity/Assets/Editor.meta`
- Create: `Ros2Unity/Assets/Editor/ROSettaDDSPerfPlayerBuilder.cs`
- Create: `Ros2Unity/Assets/Editor/ROSettaDDSPerfPlayerBuilder.cs.meta`

- [ ] **Step 1: Add batch build method**

Create `ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.Build` with command line options:

```text
--rosettadds-perf-build-path <path>
--rosettadds-perf-build-target StandaloneLinux64|StandaloneOSX
--rosettadds-perf-backend il2cpp|mono
```

Build options must include `BuildOptions.Development`. Scenes come from `EditorBuildSettings.scenes`; if none are enabled, use `Assets/Scenes/SampleScene.unity`.

- [ ] **Step 2: Verify method can be discovered**

Run:

```bash
"$UNITY_EDITOR" -batchmode -nographics -projectPath "$PWD/Ros2Unity" -quit -executeMethod ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.PrintUsage -logFile artifacts/unity/perf-builder-smoke.log
```

Expected: log contains usage text and Unity exits 0.

- [ ] **Step 3: Commit**

```bash
git add Ros2Unity/Assets/Editor Ros2Unity/Assets/Editor.meta
git commit -m "feat: Profiler 計測用 Player ビルド method を追加"
```

---

### Task 4: Add .NET external perf runner

**Files:**
- Create: `tools/rosettadds-perf-runner/rosettadds-perf-runner.csproj`
- Create: `tools/rosettadds-perf-runner/Program.cs`
- Create: `tools/rosettadds-perf-runner/RunnerOptions.cs`
- Create: `tools/rosettadds-perf-runner/PerfScenario.cs`
- Create: `tools/rosettadds-perf-runner/ProcessCapture.cs`
- Create: `tools/rosettadds-perf-runner/Ros2HelperEvent.cs`
- Create: `tools/rosettadds-perf-runner/ArtifactManifest.cs`

- [ ] **Step 1: Add runner options and scenario model**

Support:

```text
--backend il2cpp|mono
--build-target StandaloneLinux64|StandaloneOSX
--scenario <name|all>
--unity-editor <path>
--helper <path>
--artifacts <path>
--capture-frames <int>
--profiler-memory <bytes>
--skip-build
```

Initial scenarios are:

- `unity-to-ros2-reliable-32`
- `unity-to-ros2-reliable-1024`
- `unity-to-ros2-best-effort-8192`
- `ros2-to-unity-reliable-32`
- `ros2-to-unity-reliable-1024`
- `ros2-to-unity-best-effort-8192`

- [ ] **Step 2: Add process capture**

Run child processes with redirected stdout / stderr, timeout, exit code capture, and log files in the scenario artifact directory.

- [ ] **Step 3: Add Unity build orchestration**

Invoke Unity Editor with `-executeMethod ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.Build`. Do not pass ROS 2 environment requirements to Unity.

- [ ] **Step 4: Add scenario orchestration**

For `unity_to_ros2`, start helper subscriber first, wait `ready`, then start Player with profiler arguments. For `ros2_to_unity`, start Player first, wait ready file, start helper publisher with `--measure-start`, wait `armed`, send measure start.

- [ ] **Step 5: Add artifact manifest**

Write `manifest.json` with runner options, scenario list, process exit codes, metrics path, Profiler raw path, and log paths.

- [ ] **Step 6: Verify runner unit-level behavior**

Run:

```bash
dotnet build tools/rosettadds-perf-runner/rosettadds-perf-runner.csproj
dotnet run --project tools/rosettadds-perf-runner -- --help
```

Expected: build succeeds and help exits 0.

- [ ] **Step 7: Commit**

```bash
git add tools/rosettadds-perf-runner
git commit -m "feat: Unity Player Profiler 計測 runner を追加"
```

---

### Task 5: Wire documentation and smoke verification

**Files:**
- Modify: `docs/unity-verification.md`
- Modify: `docs/interop.md`

- [ ] **Step 1: Document new workflow**

Document:

```bash
nix develop
scripts/ros2/build_helper.sh
dotnet run --project tools/rosettadds-perf-runner -- --scenario unity-to-ros2-reliable-1024
```

Explain artifact layout:

```text
artifacts/perf/<run-id>/manifest.json
artifacts/perf/<run-id>/<scenario>/player.profiler.raw
artifacts/perf/<run-id>/<scenario>/metrics.ndjson
artifacts/perf/<run-id>/<scenario>/player.log
artifacts/perf/<run-id>/<scenario>/helper.stdout.ndjson
artifacts/perf/<run-id>/<scenario>/helper.stderr.log
```

- [ ] **Step 2: Run repository checks**

Run:

```bash
dotnet test rosettadds.sln
scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests
.github/scripts/check_unity_meta.sh
```

Expected: tests pass and Unity meta check reports no missing / orphan meta files.

- [ ] **Step 3: Run one end-to-end scenario when environment is available**

Run:

```bash
nix develop
scripts/ros2/build_helper.sh
dotnet run --project tools/rosettadds-perf-runner -- --scenario unity-to-ros2-reliable-1024 --capture-frames 1200
```

Expected: artifact directory contains manifest, metrics, logs, and `player.profiler.raw`.

- [ ] **Step 4: Commit**

```bash
git add docs/unity-verification.md docs/interop.md
git commit -m "docs: Player Profiler 計測手順を更新"
```
