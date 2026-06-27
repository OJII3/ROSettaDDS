# Reliable Discovery Capture Investigation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sony XIG04 (Xperia 10 III / Android 15) 上の Unity Player (rosettadds 独自 RTPS) と helper (rmw_fastrtts_cpp) 間の reliable QoS discovery (SPDP/SEDP) 不通 (findings B) の根本原因を、host 側 tshark + Android 側 tcpdump のマルチキャストパケットキャプチャで特定する。

**Architecture:** 既存 perf-runner を `--scenario unity-to-ros2-reliable-32` と `--scenario unity-to-ros2-best-effort-8192` (control) で 2 回計測し、計測中に host 側 wlan0 と Android 側 wlan0 の両方でマルチキャストキャプチャ (UDP 7400-12500) を取得する。pcap を tshark + Python で解析し、port 別 / src IP 別 / 5 秒 window 別の packet count + RTPS submessage 内訳を抽出。3 仮説 (Player 側 SPDP/SEDP 不出力 / Android kernel 経路問題 / host 側 rmw_fastrtts_cpp parse 失敗) の支持 / 反証データを整理し、findings doc として 1 commit + 1 PR で main にマージする。

**Tech Stack:** tshark 4.x / tcpdump 4.99.6 / Python 3.11+ (pandas) / nix devShell の既存 `~/.nix-profile/bin/{tshark,tcpdump}` / `tools/rosettadds-perf-runner` (既存) / adb shell su / Sony XIG04 (Xperia 10 III) USB 接続

**Design doc:** `docs/superpowers/specs/2026-06-27-discovery-capture-investigation-design.md`

---

## 変更 / 作成ファイル一覧

| 操作 | パス | 役割 |
| --- | --- | --- |
| Create | `artifacts/discovery-capture/.gitignore` | pcap / 一時ファイルを git 管理外 |
| Create | `artifacts/discovery-capture/<runId>/analyze.py` | tshark で 4 pcap を CSV 化、port 別 / src 別集計 |
| Create | `artifacts/discovery-capture/<runId>/README.md` | 各 runId の計測手順サマリ |
| Create | `tools/discovery-capture/discovery_capture.py` | 両側 capture 起動 + clock sync + pcap pull ヘルパ |
| Create | `tools/discovery-capture/tests/test_discovery_capture.py` | discovery_capture.py の CLI フラグ parse テスト (xUnit / pytest) |
| Modify | `rosettadds.sln` | test project を追加 (任意) |
| Create | `docs/superpowers/specs/2026-06-27-discovery-capture-investigation.md` | findings doc (本 PR の主成果物) |

Unity / RTPS / DDS のコードは触らない。perf-runner も無修正。

---

## Task 1: 計測ディレクトリ + .gitignore 準備

**Files:**
- Create: `artifacts/discovery-capture/.gitignore`

- [ ] **Step 1.1: 計測ディレクトリ作成**

```bash
mkdir -p artifacts/discovery-capture
```

期待: ディレクトリが作成され、書き込み可能。

- [ ] **Step 1.2: .gitignore 作成**

`artifacts/discovery-capture/.gitignore` を新規作成し、以下を記述する:

```
# pcap と巨大一時ファイルは git 管理外
*.pcap
*.pcapng
*.tsv
*.log
!README.md
```

- [ ] **Step 1.3: 動作確認**

```bash
touch artifacts/discovery-capture/test.pcap
git check-ignore -v artifacts/discovery-capture/test.pcap
```

期待: `artifacts/discovery-capture/.gitignore:3:*.pcap` が表示される (無視される)。

- [ ] **Step 1.4: 動作確認の cleanup**

```bash
rm artifacts/discovery-capture/test.pcap
```

- [ ] **Step 1.5: commit**

```bash
git add artifacts/discovery-capture/.gitignore
git commit -m "chore: discovery-capture 計測ディレクトリ + .gitignore 追加"
```

---

## Task 2: capture ヘルパ CLI フラグ parse テスト (TDD)

**Files:**
- Create: `tools/discovery-capture/tests/test_discovery_capture.py`
- Create: `tools/discovery-capture/discovery_capture.py`

- [ ] **Step 2.1: test project ディレクトリ作成**

```bash
mkdir -p tools/discovery-capture/tests
```

- [ ] **Step 2.2: pytest テスト作成 (失敗する状態)**

`tools/discovery-capture/tests/test_discovery_capture.py` を新規作成し、以下を記述する:

```python
import pytest
from discovery_capture import parse_args


def test_parse_args_reliable_scenario():
    args = parse_args([
        "--host-interface", "wlan0",
        "--android-device", "5HF6OVWCDECMJZ59",
        "--scenario", "unity-to-ros2-reliable-32",
        "--run-id", "20260627-180000",
    ])
    assert args.host_interface == "wlan0"
    assert args.android_device == "5HF6OVWCDECMJZ59"
    assert args.scenario == "unity-to-ros2-reliable-32"
    assert args.run_id == "20260627-180000"
    assert args.udp_portrange == "7400-12500"  # default


def test_parse_args_custom_portrange():
    args = parse_args([
        "--host-interface", "wlan0",
        "--android-device", "5HF6OVWCDECMJZ59",
        "--scenario", "unity-to-ros2-reliable-32",
        "--run-id", "20260627-180000",
        "--udp-portrange", "7400-7500",
    ])
    assert args.udp_portrange == "7400-7500"


def test_parse_args_missing_android_device():
    with pytest.raises(SystemExit):
        parse_args([
            "--host-interface", "wlan0",
            "--scenario", "unity-to-ros2-reliable-32",
            "--run-id", "20260627-180000",
        ])
```

- [ ] **Step 2.3: テスト実行 → FAIL 確認**

```bash
cd tools/discovery-capture
PYTHONPATH=. python3 -m pytest tests/test_discovery_capture.py -v
```

期待: `ModuleNotFoundError: No module named 'discovery_capture'`

- [ ] **Step 2.4: 最小実装 (argparse だけ)**

`tools/discovery-capture/discovery_capture.py` を新規作成し、以下を記述する:

```python
import argparse


def parse_args(argv=None):
    parser = argparse.ArgumentParser(
        description="両側 capture 起動 + pcap pull ヘルパ"
    )
    parser.add_argument("--host-interface", required=True)
    parser.add_argument("--android-device", required=True)
    parser.add_argument("--scenario", required=True,
                        choices=["unity-to-ros2-reliable-32",
                                 "unity-to-ros2-best-effort-8192",
                                 "ros2-to-unity-reliable-32",
                                 "ros2-to-unity-best-effort-32k"])
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--udp-portrange", default="7400-12500")
    return parser.parse_args(argv)


if __name__ == "__main__":
    args = parse_args()
    print(f"host={args.host_interface} android={args.android_device} "
          f"scenario={args.scenario} run_id={args.run_id}")
```

- [ ] **Step 2.5: テスト実行 → PASS 確認**

```bash
cd tools/discovery-capture
PYTHONPATH=. python3 -m pytest tests/test_discovery_capture.py -v
```

期待: 3 tests passed。

- [ ] **Step 2.6: commit**

```bash
git add tools/discovery-capture/
git commit -m "feat(discovery-capture): CLI フラグ parse ヘルパを追加 (TDD)"
```

---

## Task 3: discovery_capture.py 拡張 (capture 起動 + pcap pull)

**Files:**
- Modify: `tools/discovery-capture/discovery_capture.py`
- Modify: `tools/discovery-capture/tests/test_discovery_capture.py`

- [ ] **Step 3.1: pcap path 計算関数のテスト追加**

`tools/discovery-capture/tests/test_discovery_capture.py` に以下を追加:

```python
from discovery_capture import build_paths, CapturePaths


def test_build_paths_reliable():
    args = parse_args([
        "--host-interface", "wlan0",
        "--android-device", "5HF6OVWCDECMJZ59",
        "--scenario", "unity-to-ros2-reliable-32",
        "--run-id", "20260627-180000",
    ])
    paths = build_paths(args, root="/srv/run")
    assert isinstance(paths, CapturePaths)
    assert paths.host_pcap == "/srv/run/host-reliable.pcap"
    assert paths.android_pcap == "/srv/run/android-reliable.pcap"
    assert paths.host_log == "/srv/run/host-tshark.log"
    assert paths.android_log == "/srv/run/android-tcpdump.log"
    assert paths.clock_pre_host == "/srv/run/host-clock-pre.txt"
    assert paths.clock_pre_android == "/srv/run/android-clock-pre.txt"


def test_build_paths_best_effort():
    args = parse_args([
        "--host-interface", "wlan0",
        "--android-device", "5HF6OVWCDECMJZ59",
        "--scenario", "unity-to-ros2-best-effort-8192",
        "--run-id", "20260627-180000",
    ])
    paths = build_paths(args, root="/srv/run")
    assert paths.host_pcap == "/srv/run/host-best-effort.pcap"
    assert paths.android_pcap == "/srv/run/android-best-effort.pcap"
```

- [ ] **Step 3.2: テスト実行 → FAIL 確認**

```bash
cd tools/discovery-capture
PYTHONPATH=. python3 -m pytest tests/test_discovery_capture.py -v
```

期待: `ImportError: cannot import name 'build_paths' from 'discovery_capture'`

- [ ] **Step 3.3: build_paths 実装追加**

`tools/discovery-capture/discovery_capture.py` を以下に拡張 (parse_args の後に追加):

```python
from dataclasses import dataclass


@dataclass
class CapturePaths:
    host_pcap: str
    android_pcap: str
    host_log: str
    android_log: str
    clock_pre_host: str
    clock_pre_android: str


def build_paths(args, root: str) -> CapturePaths:
    tag = "reliable" if "reliable" in args.scenario else "best-effort"
    return CapturePaths(
        host_pcap=f"{root}/host-{tag}.pcap",
        android_pcap=f"{root}/android-{tag}.pcap",
        host_log=f"{root}/host-tshark.log",
        android_log=f"{root}/android-tcpdump.log",
        clock_pre_host=f"{root}/host-clock-pre.txt",
        clock_pre_android=f"{root}/android-clock-pre.txt",
    )
```

- [ ] **Step 3.4: テスト実行 → PASS 確認**

```bash
cd tools/discovery-capture
PYTHONPATH=. python3 -m pytest tests/test_discovery_capture.py -v
```

期待: 5 tests passed。

- [ ] **Step 3.5: commit**

```bash
git add tools/discovery-capture/
git commit -m "feat(discovery-capture): pcap path 計算 (build_paths) を追加"
```

---

## Task 4: Android root 取得 + tcpdump 動作確認 (手動)

> 注: このタスクは device 状態確認のため手動実行。自動テストは不可。

- [ ] **Step 4.1: device 接続確認**

```bash
adb devices
```

期待: `5HF6OVWCDECMJZ59    device` の行。

- [ ] **Step 4.2: root 取得確認**

```bash
adb -s 5HF6OVWCDECMJZ59 shell su -c 'id'
```

期待: `uid=0(root)` を含む出力。失敗するなら `su: not found` または `Permission denied`。
失敗時: `findings doc` の limitations に「root 取得不可」と明記し、本調査をスキップ。

- [ ] **Step 4.3: tcpdump 既存 / 静的バイナリ push**

```bash
# 既存 tcpdump 確認
adb -s 5HF6OVWCDECMJZ59 shell su -c 'which tcpdump'
# 存在しなければ push
if ! adb -s 5HF6OVWCDECMJZ59 shell su -c 'which tcpdump' 2>/dev/null; then
  adb -s 5HF6OVWCDECMJZ59 push ~/.nix-profile/bin/tcpdump /data/local/tmp/tcpdump
  adb -s 5HF6OVWCDECMJZ59 shell su -c 'chmod 755 /data/local/tmp/tcpdump'
fi
```

期待: 最終的に `tcpdump --version` が実行可能。

- [ ] **Step 4.4: IGMP join 確認**

```bash
adb -s 5HF6OVWCDECMJZ59 shell su -c 'ip maddr show wlan0 | grep 239.255.0.1'
```

期待: `239.255.0.1` を含む行。無い場合は本調査の findings に limitation として記録。

- [ ] **Step 4.5: 確認結果のメモ**

`artifacts/discovery-capture/pre-check.txt` に以下を保存:

```
device: 5HF6OVWCDECMJZ59
root: ok
tcpdump: /data/local/tmp/tcpdump
igmp_239.255.0.1: joined | not-joined
host_tshark: $(tshark --version | head -1)
host_tcpdump: $(tcpdump --version | head -1)
```

- [ ] **Step 4.6: commit**

```bash
git add artifacts/discovery-capture/pre-check.txt
git commit -m "chore: discovery-capture 計測前 device 状態確認を記録"
```

---

## Task 5: reliable-32 capture 起動 + 計測

- [ ] **Step 5.1: 計測ディレクトリ作成**

```bash
RUN_ID=$(date +%Y%m%d-%H%M%S)
RUN_DIR="artifacts/discovery-capture/$RUN_ID"
mkdir -p "$RUN_DIR"
echo "RUN_ID=$RUN_ID" > "$RUN_DIR/env.sh"
echo "RUN_DIR=$RUN_DIR" >> "$RUN_DIR/env.sh"
```

期待: `RUN_DIR` 変数が echo される。

- [ ] **Step 5.2: 既存 Android build artifact 確認**

```bash
ls -la /tmp/rosettadds-perf-android.apk
```

期待: ファイルが存在しサイズが >40MB。`97e446e` 取り込み済みを期待。
無ければ `dotnet run --project tools/rosettadds-perf-runner -c Release -- --build-target Android --scenario all --android-device 5HF6OVWCDECMJZ59 --artifacts /tmp/build-check` で再 build (10-20 分)。

- [ ] **Step 5.3: host tshark 起動 (バックグラウンド)**

```bash
source "$RUN_DIR/env.sh"
tshark -i wlan0 -f "udp portrange 7400-12500" \
  -w "$RUN_DIR/host-reliable.pcap" -b duration:300 \
  > "$RUN_DIR/host-tshark.log" 2>&1 &
echo $! > "$RUN_DIR/host-tshark.pid"
sleep 2
ps -p $(cat "$RUN_DIR/host-tshark.pid") -o pid,comm
```

期待: tshark プロセスが動作中。`tshark` を含む comm。

- [ ] **Step 5.4: Android tcpdump 起動 (バックグラウンド)**

```bash
source "$RUN_DIR/env.sh"
adb -s 5HF6OVWCDECMJZ59 shell su -c \
  "/data/local/tmp/tcpdump -i wlan0 -n -B 4096 \
   'udp portrange 7400-12500' -w /sdcard/android-reliable.pcap -U" \
  > "$RUN_DIR/android-tcpdump.log" 2>&1 &
echo $! > "$RUN_DIR/android-tcpdump.pid"
sleep 3
ps -p $(cat "$RUN_DIR/android-tcpdump.pid") -o pid,comm
```

期待: tcpdump プロセスが動作中。`tcpdump` を含む comm。
`Operation not permitted` の場合は root 取得失敗 → findings に limitation 記録。

- [ ] **Step 5.5: クロック同期確認**

```bash
source "$RUN_DIR/env.sh"
echo $(date +%s.%N) > "$RUN_DIR/host-clock-pre.txt"
adb -s 5HF6OVWCDECMJZ59 shell 'date +%s.%N' > "$RUN_DIR/android-clock-pre.txt"
cat "$RUN_DIR/host-clock-pre.txt" "$RUN_DIR/android-clock-pre.txt"
```

期待: 2 つの timestamp の差が 1 秒以内 (理想は 100ms 以内)。

- [ ] **Step 5.6: reliable-32 計測実行**

```bash
source "$RUN_DIR/env.sh"
dotnet run --project tools/rosettadds-perf-runner -c Release --no-build -- \
  --build-target Android \
  --scenario unity-to-ros2-reliable-32 \
  --android-device 5HF6OVWCDECMJZ59 \
  --artifacts "$RUN_DIR/reliable-32" \
  --capture-frames 200
```

期待: 計測完了。`helper received 0/500` (想定通り B 仮説支持)。

- [ ] **Step 5.7: capture 停止**

```bash
source "$RUN_DIR/env.sh"
kill $(cat "$RUN_DIR/android-tcpdump.pid") 2>/dev/null
kill $(cat "$RUN_DIR/host-tshark.pid") 2>/dev/null
sleep 2
ls -la "$RUN_DIR"/*.pcap
```

期待: `host-reliable.pcap` / `android-reliable.pcap` がそれぞれ >1KB 存在。

- [ ] **Step 5.8: Android pcap pull**

```bash
source "$RUN_DIR/env.sh"
adb -s 5HF6OVWCDECMJZ59 shell su -c \
  'cat /sdcard/android-reliable.pcap' > "$RUN_DIR/android-reliable.pcap"
adb -s 5HF6OVWCDECMJZ59 shell su -c 'rm /sdcard/android-reliable.pcap'
ls -la "$RUN_DIR/android-reliable.pcap"
```

期待: ファイルサイズが `1KB` 以上。`Permission denied` の場合は
`adb -s ... shell su -c 'chmod 666 /sdcard/android-reliable.pcap'` で permission 変更後再試行。

- [ ] **Step 5.9: pcap 破損チェック**

```bash
source "$RUN_DIR/env.sh"
tshark -c 1 -r "$RUN_DIR/host-reliable.pcap" 2>&1 | head -3
tshark -c 1 -r "$RUN_DIR/android-reliable.pcap" 2>&1 | head -3
```

期待: 両 pcap とも 1 packet 以上の出力が得られる。`pcap: file has no valid packets` の場合は再 capture。

- [ ] **Step 5.10: 計測結果の簡易集計 (host 側のみ)**

```bash
source "$RUN_DIR/env.sh"
tshark -r "$RUN_DIR/host-reliable.pcap" -Y "rtps" \
  -T fields -e udp.dstport | sort | uniq -c | sort -rn
```

期待: port 別 count の表。`7400` (SPDP multicast) / `7401` (user multicast) /
`7410+2*PID` (SEDP) 等の数字が見える。

- [ ] **Step 5.11: commit (計測 data 以外)**

```bash
source "$RUN_DIR/env.sh"
git add "$RUN_DIR/env.sh" "$RUN_DIR/host-tshark.log" \
        "$RUN_DIR/android-tcpdump.log" "$RUN_DIR/host-clock-pre.txt" \
        "$RUN_DIR/android-clock-pre.txt" "$RUN_DIR/reliable-32/manifest.json" 2>/dev/null || true
# pcap 自体は .gitignore で除外される
git commit -m "chore: discovery-capture reliable-32 計測 (host / android pcap 取得)" || \
  echo "no changes to commit (data only)"
```

---

## Task 6: best-effort-8192 capture (control) + 計測

- [ ] **Step 6.1: host tshark 起動 (best-effort 用)**

```bash
source "$RUN_DIR/env.sh"
tshark -i wlan0 -f "udp portrange 7400-12500" \
  -w "$RUN_DIR/host-best-effort.pcap" -b duration:300 \
  > "$RUN_DIR/host-tshark-best-effort.log" 2>&1 &
echo $! > "$RUN_DIR/host-tshark-be.pid"
sleep 2
ps -p $(cat "$RUN_DIR/host-tshark-be.pid") -o pid,comm
```

- [ ] **Step 6.2: Android tcpdump 起動 (best-effort 用)**

```bash
source "$RUN_DIR/env.sh"
adb -s 5HF6OVWCDECMJZ59 shell su -c \
  "/data/local/tmp/tcpdump -i wlan0 -n -B 4096 \
   'udp portrange 7400-12500' -w /sdcard/android-best-effort.pcap -U" \
  > "$RUN_DIR/android-tcpdump-be.log" 2>&1 &
echo $! > "$RUN_DIR/android-tcpdump-be.pid"
sleep 3
ps -p $(cat "$RUN_DIR/android-tcpdump-be.pid") -o pid,comm
```

- [ ] **Step 6.3: best-effort-8192 計測実行**

```bash
source "$RUN_DIR/env.sh"
dotnet run --project tools/rosettadds-perf-runner -c Release --no-build -- \
  --build-target Android \
  --scenario unity-to-ros2-best-effort-8192 \
  --android-device 5HF6OVWCDECMJZ59 \
  --artifacts "$RUN_DIR/best-effort-8192" \
  --capture-frames 200
```

期待: 計測完了。`helper received 73-81%` (想定通り best-effort 部分成功)。

- [ ] **Step 6.4: capture 停止 + pcap pull + 破損チェック**

```bash
source "$RUN_DIR/env.sh"
kill $(cat "$RUN_DIR/android-tcpdump-be.pid") 2>/dev/null
kill $(cat "$RUN_DIR/host-tshark-be.pid") 2>/dev/null
sleep 2
adb -s 5HF6OVWCDECMJZ59 shell su -c \
  'cat /sdcard/android-best-effort.pcap' > "$RUN_DIR/android-best-effort.pcap"
adb -s 5HF6OVWCDECMJZ59 shell su -c 'rm /sdcard/android-best-effort.pcap'
tshark -c 1 -r "$RUN_DIR/host-best-effort.pcap" 2>&1 | head -3
tshark -c 1 -r "$RUN_DIR/android-best-effort.pcap" 2>&1 | head -3
```

期待: 両 pcap とも読み込み可能。

- [ ] **Step 6.5: 計測結果の簡易集計 (host 側のみ)**

```bash
source "$RUN_DIR/env.sh"
tshark -r "$RUN_DIR/host-best-effort.pcap" -Y "rtps" \
  -T fields -e udp.dstport | sort | uniq -c | sort -rn
```

期待: reliable と比較し、user data port 7401 の count が best-effort で 1 以上 (best-effort 部分的成功)。

- [ ] **Step 6.6: commit**

```bash
source "$RUN_DIR/env.sh"
git add "$RUN_DIR/host-tshark-best-effort.log" \
        "$RUN_DIR/android-tcpdump-be.log" \
        "$RUN_DIR/best-effort-8192/manifest.json" 2>/dev/null || true
git commit -m "chore: discovery-capture best-effort-8192 計測 (control)" || \
  echo "no changes to commit (data only)"
```

---

## Task 7: analyze.py 作成 (TDD)

**Files:**
- Create: `tools/discovery-capture/analyze.py`
- Create: `tools/discovery-capture/tests/test_analyze.py`

- [ ] **Step 7.1: tshark CSV 抽出関数のテスト作成**

`tools/discovery-capture/tests/test_analyze.py` を新規作成:

```python
import pytest
from analyze import parse_tshark_csv, summarize_ports


SAMPLE_CSV = """1700000000.123456\t192.168.0.22\t239.255.0.1\t12345\t7400\t0x01
1700000000.234567\t192.168.0.20\t239.255.0.1\t54321\t7400\t0x01
1700000000.345678\t192.168.0.22\t239.255.0.1\t12345\t7410\t0x07
1700000001.000000\t192.168.0.22\t239.255.0.1\t12345\t7401\t0x15
"""


def test_parse_tshark_csv_basic():
    rows = parse_tshark_csv(SAMPLE_CSV)
    assert len(rows) == 4
    assert rows[0]["src"] == "192.168.0.22"
    assert rows[0]["dport"] == 7400
    assert rows[2]["sm_id"] == "0x07"


def test_summarize_ports():
    rows = parse_tshark_csv(SAMPLE_CSV)
    summary = summarize_ports(rows)
    assert summary[7400] == 2
    assert summary[7410] == 1
    assert summary[7401] == 1


def test_summarize_src():
    from analyze import summarize_src
    rows = parse_tshark_csv(SAMPLE_CSV)
    summary = summarize_src(rows)
    assert summary["192.168.0.22"] == 3
    assert summary["192.168.0.20"] == 1
```

- [ ] **Step 7.2: テスト実行 → FAIL 確認**

```bash
cd tools/discovery-capture
PYTHONPATH=. python3 -m pytest tests/test_analyze.py -v
```

期待: `ModuleNotFoundError: No module named 'analyze'`

- [ ] **Step 7.3: analyze.py 実装**

`tools/discovery-capture/analyze.py` を新規作成:

```python
import csv
import io
from collections import Counter
from typing import Dict, List


def parse_tshark_csv(text: str) -> List[Dict[str, object]]:
    """tshark -T fields の TSV 出力を dict の list に変換。"""
    rows: List[Dict[str, object]] = []
    reader = csv.reader(io.StringIO(text), delimiter="\t")
    for parts in reader:
        if len(parts) < 6:
            continue
        rows.append({
            "ts": parts[0],
            "src": parts[1],
            "dst": parts[2],
            "sport": int(parts[3]) if parts[3] else 0,
            "dport": int(parts[4]) if parts[4] else 0,
            "sm_id": parts[5],
        })
    return rows


def summarize_ports(rows: List[Dict[str, object]]) -> Dict[int, int]:
    return dict(Counter(r["dport"] for r in rows))


def summarize_src(rows: List[Dict[str, object]]) -> Dict[str, int]:
    return dict(Counter(r["src"] for r in rows))


def extract_pcap(pcap_path: str) -> List[Dict[str, object]]:
    import subprocess
    out = subprocess.check_output([
        "tshark", "-r", pcap_path, "-Y", "rtps",
        "-T", "fields",
        "-e", "frame.time_epoch",
        "-e", "ip.src", "-e", "ip.dst",
        "-e", "udp.srcport", "-e", "udp.dstport",
        "-e", "rtps.sm.id",
    ], text=True)
    return parse_tshark_csv(out)
```

- [ ] **Step 7.4: テスト実行 → PASS 確認**

```bash
cd tools/discovery-capture
PYTHONPATH=. python3 -m pytest tests/test_analyze.py -v
```

期待: 3 tests passed。

- [ ] **Step 7.5: 計測 pcap で動作確認**

```bash
source "$RUN_DIR/env.sh"
cd tools/discovery-capture
PYTHONPATH=. python3 -c "
from analyze import extract_pcap, summarize_ports, summarize_src
for name in ['host-reliable', 'android-reliable', 'host-best-effort', 'android-best-effort']:
    pcap = '$RUN_DIR/' + name + '.pcap'
    rows = extract_pcap(pcap)
    print(f'== {name} ==')
    print('  ports:', summarize_ports(rows))
    print('  src:  ', summarize_src(rows))
"
```

期待: 4 ファイルすべてで port / src 集計が出力される。

- [ ] **Step 7.6: commit**

```bash
git add tools/discovery-capture/
git commit -m "feat(discovery-capture): pcap 解析スクリプト (analyze.py) を追加 (TDD)"
```

---

## Task 8: 仮説判定 + 集計表作成

- [ ] **Step 8.1: 集計 CSV 出力**

```bash
source "$RUN_DIR/env.sh"
cd tools/discovery-capture
PYTHONPATH=. python3 -c "
from analyze import extract_pcap, summarize_ports, summarize_src
results = {}
for name in ['host-reliable', 'android-reliable', 'host-best-effort', 'android-best-effort']:
    pcap = '$RUN_DIR/' + name + '.pcap'
    rows = extract_pcap(pcap)
    results[name] = {
        'ports': summarize_ports(rows),
        'src': summarize_src(rows),
        'total': len(rows),
    }
import json
with open('$RUN_DIR/analysis.json', 'w') as f:
    json.dump(results, f, indent=2)
print(json.dumps(results, indent=2))
"
```

期待: 4 ファイルの集計 JSON が `$RUN_DIR/analysis.json` に出力。

- [ ] **Step 8.2: 仮説判定表作成**

```bash
cat > "$RUN_DIR/hypothesis-table.md" <<'EOF'
| 観点 | reliable-32 host | reliable-32 android | best-effort-8192 host | best-effort-8192 android |
|------|------------------|---------------------|------------------------|---------------------------|
| total packets | n | n | n | n |
| port 7400 (SPDP) | n | n | n | n |
| port 7401 (user mc) | n | n | n | n |
| port 7410 (SEDP) | n | n | n | n |
| src 192.168.0.22 (Android) | n | n | n | n |
| src 192.168.0.20 (host) | n | n | n | n |
EOF
```

期待: 空の表が作成される。Step 8.3 で実数を埋める。

- [ ] **Step 8.3: analysis.json の値で表を更新 (手動)**

```bash
source "$RUN_DIR/env.sh"
# analysis.json から値を取得し、表を更新 (テキストエディタで埋める)
cat "$RUN_DIR/analysis.json" | python3 -m json.tool
```

- [ ] **Step 8.4: 仮説判定 (手動)**

`$RUN_DIR/hypothesis-judgment.md` に以下を埋める:

```markdown
# 仮説判定 (2026-06-27)

## 仮説 1: Player 側 SPDP/SEDP 不出力

**支持 / 反証データ**:
- host-reliable.pcap: port 7400 (SPDP) count = n
- android-reliable.pcap: port 7400 (SPDP) count = n

**判定**: 支持 / 反証 / 不確定

## 仮説 2: Android kernel 経路問題 (IGMP / multicast routing)

**支持 / 反証データ**:
- host-reliable.pcap: src 192.168.0.22 count = n
- android-reliable.pcap: total packets = n
- IGMP join 状況: joined / not-joined

**判定**: 支持 / 反証 / 不確定

## 仮説 3: host 側 rmw_fastrtts_cpp parse 失敗

**支持 / 反証データ**:
- host-reliable.pcap: port 7400 (SPDP) count = n (helper が SPDP を見ていれば host 側送信ある)
- helper 受信 count: reliable 0/500 vs best-effort 73-81%

**判定**: 支持 / 反証 / 不確定
```

- [ ] **Step 8.5: 集計 + 判定の commit**

```bash
source "$RUN_DIR/env.sh"
git add "$RUN_DIR/analysis.json" "$RUN_DIR/hypothesis-table.md" \
        "$RUN_DIR/hypothesis-judgment.md"
git commit -m "docs(specs): discovery-capture 仮説判定 + 集計表" || true
```

---

## Task 9: findings doc 作成

**Files:**
- Create: `docs/superpowers/specs/2026-06-27-discovery-capture-investigation.md`

- [ ] **Step 9.1: findings テンプレート作成**

`docs/superpowers/specs/2026-06-27-discovery-capture-investigation.md` を新規作成し、
以下テンプレートを埋める:

```markdown
# Reliable Discovery Capture Investigation Findings (2026-06-27)

## サマリ

(run の結果 + 判定を 1 段落で)

## 計測環境

- 日時: 2026-06-27
- HEAD: 3c74790
- branch: perf/discovery-capture-investigation
- device: Sony XIG04 (Xperia 10 III)
- helper: rmw_fastrtts_cpp on host
- 計測 run: $RUN_ID

## 計測結果

### 4 pcap 概要

| file | size | total packets | 主な dst port |
|------|------|---------------|---------------|
| host-reliable.pcap | n MB | n | 7400: n, 7401: n, 7410: n |
| android-reliable.pcap | n MB | n | 7400: n, 7401: n, 7410: n |
| host-best-effort.pcap | n MB | n | 7400: n, 7401: n, 7410: n |
| android-best-effort.pcap | n MB | n | 7400: n, 7401: n, 7410: n |

### port 別 / src 別 packet count (Step 8.1 analysis.json)

(analysis.json の内容を表化)

### 仮説判定 (Step 8.4 hypothesis-judgment.md)

(hypothesis-judgment.md の内容を転記)

## 結論

(3 仮説のいずれが支持されたか / どれでもないか)

## 既存 findings との対応

| 既存 finding | 今回の結論 |
|--------------|-----------|
| B. Android → helper reliable discovery 不通 | (結果に応じて更新) |
| A'. Android reliable 8 KB の 71× 退化 | fragmentation コストが支配的、本調査では扱わず |

## 残存ボトルネック / next action

(判明した root cause に応じて next action を 1-3 項目)

1. (root cause への修正案)
2. (検証追加計測の必要性)
3. (他 PR への切り出し)

## 計測方法 (再現手順)

```bash
# branch 作成 + 準備
git checkout -b perf/discovery-capture-investigation

# Task 4-8 の手順を順次実行
# (詳細は plan を参照)
```

## 計測 artifact

- $RUN_DIR/host-reliable.pcap
- $RUN_DIR/android-reliable.pcap
- $RUN_DIR/host-best-effort.pcap
- $RUN_DIR/android-best-effort.pcap
- $RUN_DIR/analysis.json
- $RUN_DIR/hypothesis-table.md
- $RUN_DIR/hypothesis-judgment.md
```

- [ ] **Step 9.2: プレースホルダ scan**

```bash
grep -nE "TBD|TODO|fill in|xxxx" docs/superpowers/specs/2026-06-27-discovery-capture-investigation.md
```

期待: 出力なし。`TBD` 等があれば埋めて再実行。

- [ ] **Step 9.3: 内部整合性チェック**

- [ ] サマリ / 結論 / next action が矛盾していないこと
- [ ] port 数字と port 別 packet count が一致すること
- [ ] 仮説判定と既存 findings (B + A') の対応が取れていること

- [ ] **Step 9.4: 曖昧性チェック**

各段落が 1 通りに解釈できることを確認。曖昧なら言い切り形に修正。

- [ ] **Step 9.5: commit**

```bash
git add docs/superpowers/specs/2026-06-27-discovery-capture-investigation.md
git commit -m "docs(specs): discovery-capture 計測結果と findings を追加"
```

---

## Task 10: 検証 + PR

- [ ] **Step 10.1: Validation チェックリスト実行**

design doc の Validation 完了条件 10 項目を 1 つずつ確認:

- [ ] 4 pcap 取得 (1 KB 以上)
- [ ] 4 pcap 破損なし
- [ ] host-reliable.pcap に Android からの SPDP あり
- [ ] android-reliable.pcap に Player からの SPDP あり
- [ ] host-reliable.pcap に host からの SPDP reply あり
- [ ] host-best-effort.pcap に Android からの user data あり
- [ ] port / src 別 packet count 表あり
- [ ] 3 仮説に支持 / 反証データあり
- [ ] 1 commit + 1 PR で main マージ可能
- [ ] `git status` clean

- [ ] **Step 10.2: 既存 spec doc との整合性確認**

`docs/superpowers/specs/2026-06-27-android-bottleneck-investigation.md` の
「Next action 1. Android → helper reliable discovery 不通 + ACK 待ち block の原因調査 (B + A')」
の項目が、本 findings により更新されたか確認。更新されていないなら、
本 PR の summary で「B は本 findings を参照」と明記。

- [ ] **Step 10.3: ブランチ push**

```bash
git push origin perf/discovery-capture-investigation
```

- [ ] **Step 10.4: PR 作成**

```bash
gh pr create --base main --head perf/discovery-capture-investigation \
  --title "docs(specs): Android reliable discovery 調査 (B) の findings を追加" \
  --body "本 PR は 2026-06-27-android-bottleneck-investigation.md の Next action 1 のうち B (discovery 不通) を host tshark + Android tcpdump のパケットキャプチャで解析した findings。

調査 design: docs/superpowers/specs/2026-06-27-discovery-capture-investigation-design.md
調査結果: docs/superpowers/specs/2026-06-27-discovery-capture-investigation.md

A' (reliable-8000 の 71× 退化) は fragmentation コストが支配的と推定され別 findings。"
```

- [ ] **Step 10.5: CI 通過待ち**

```bash
gh pr checks
```

期待: 全て pass。

- [ ] **Step 10.6: レビュー対応 + main マージ**

レビュー指摘に対応後、main にマージ (squash or merge、PR 設定に従う)。
