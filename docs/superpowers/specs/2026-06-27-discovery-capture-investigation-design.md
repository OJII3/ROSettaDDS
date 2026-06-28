# Reliable Discovery Capture Investigation Design (2026-06-27)

## Goal

Sony XIG04 (Xperia 10 III / Android 15 / arm64-v8a) 上の Unity Player (rosettadds 独自 RTPS) と、
LAN 上の helper (rmw_fastrtts_cpp) の間で **reliable QoS の discovery (SPDP / SEDP) が
通らない問題 (findings B)** の根本原因を、host 側 wireshark/tshark + Android 側
tcpdump によるマルチキャストパケットキャプチャで特定する。

既存 findings `2026-06-27-android-bottleneck-investigation.md` で観測された
`unity-to-ros2-reliable-32` (helper received 0/500) と
`unity-to-ros2-best-effort-8192` (helper received 73-81%) の差を、reliable と
best-effort で対称な SPDP / SEDP / user data パケットキャプチャを取って比較解析する。

計測結果と分析は `docs/superpowers/specs/2026-06-27-discovery-capture-investigation.md`
(findings) にまとめ、3 つの仮説 (Player 側 SPDP/SEDP 不出力 / Android kernel
経路問題 / host 側 rmw_fastrtts_cpp parse 失敗) の支持 / 反証データを示す。
修正が必要なら別 PR の scope を提示し、本 PR は findings のみで main マージ可能とする。

## Scope

### In scope

- `unity-to-ros2-reliable-32` 計測中の host 側マルチキャストキャプチャ
  (tshark `-i wlan0 -f "udp portrange 7400-12500"`)
- 同計測中の Android 側マルチキャストキャプチャ
  (`adb shell tcpdump -i wlan0 -n -B 4096 'udp portrange 7400-12500'`)
- best-effort control: `unity-to-ros2-best-effort-8192` を同手順で計測
- pcap 解析: port 別 / src IP 別 / 5 秒 window 別 packet count + RTPS submessage 内訳
- 3 仮説の支持 / 反証データ
- findings doc 1 commit + 1 PR

### Out of scope

- A' (reliable-8000 の 71× 退化) の解析。fragmentation コスト (8 fragments × 6ms/fragment)
  が支配的と推定され、別 findings として記録。修正には StatelessWriter への fragmentation
  追加または reliable 8KB の MaxSamples 調整が候補だが本 PR では扱わない
- helper / Player 側コードの修正。findings 後の別 PR
- 計測 runner (perf-runner) の機能追加。既存 perf-runner + 手動 adb tcpdump 起動で十分
- 別 subnet / USB テザリング / emulator 環境。同一 L2 セグメントでのみ実施
- logcat streaming runner 化、player.profiler.raw pull 対応。別 Next action

## Constraints

- **同一 L2 セグメント**: Android 端末 `192.168.0.22/24` と host `192.168.0.20/24` が
  同一 AP 配下にある既存環境を利用。ping 5.9ms、SPDP multicast `239.255.0.1` 経路
  成立済み (Android ボトルネック調査で検証)
- **root 権限**: Android 側 tcpdump は `su` 経由で実行。Xperia 10 III が
  userdebug / rooted でない場合は静的バイナリでも raw socket 取得不可。
  計測前に `adb shell su -c 'tcpdump --version'` で確認
- **host ツール**: tcpdump 4.99.6 / tshark / wireshark が nix profile に
  既導入 (`~/.nix-profile/bin/`)
- **既存 build artifact**: Android 計測用に `/tmp/rosettadds-perf-android.apk` を
  そのまま流用 (lean ProfilerRecorders + PublishRepeatedAsync 取り込み済 HEAD `3c74790`)
- **計測時間**: 1 capture × 2 scenario = 約 2-4 分、解析 30-60 分、findings 30 分
- **pcap サイズ**: filter で絞れば 4 capture 合計で 10-50 MB 想定
- **時刻同期**: host `date +%s.%N` と Android `date +%s.%N` の差を ±500ms 以内に
  収める想定。それ以下の精度は出ない

## Architecture

```
┌─────────────────────┐  tshark host     ┌────────────────────────────┐
│ host (192.168.0.20) │ ──────────────► │ host.pcap                  │
│  perf-runner        │   wlan0          │ tshark filter:             │
│  ROS 2 helper       │                  │   udp portrange 7400-12500 │
│  tshark / tcpdump   │                  │   (host 239.255.0.1)       │
└─────────────────────┘                  └──────────────┬─────────────┘
                                                      │ 解析
                                                      ▼
┌─────────────────────┐  tcpdump device  ┌────────────────────────────┐
│ Android (192.168.0.22) │ ──────────── │ android.pcap                 │
│  Unity Player        │   wlan0          │ adb shell tcpdump -i wlan0 │
│  (rosettadds RTPS)   │                  │   'udp portrange 7400-12500'│
└─────────────────────┘                  └──────────────┬─────────────┘
                                                      │ 解析
                                                      ▼
                                  host 上で tshark + Python で
                                  port 別 / src IP 別 / time window 別
                                  packet count + RTPS submessage 内訳
```

### Components

- **host tshark**: `tshark -i wlan0 -f "udp portrange 7400-12500" -w <file>`
  既存 host wlan0 (192.168.0.20) で capture。root 必須 (tshark は通常 setcap で起動可)
- **Android tcpdump**: 静的バイナリを `/data/local/tmp/tcpdump` に push、
  `adb shell su -c '/data/local/tmp/tcpdump -i wlan0 -n -B 4096
  "udp portrange 7400-12500" -w /sdcard/android.pcap -U'` で起動
- **perf-runner**: 既存 `tools/rosettadds-perf-runner` を `--scenario unity-to-ros2-reliable-32` /
  `--scenario unity-to-ros2-best-effort-8192` で起動。skip-build なし
- **分析 script**: `artifacts/discovery-capture/<runId>/analyze.py` で
  4 pcap を読み、port 別 / src IP 別 / 5 秒 window 別 count + RTPS submessage
  内訳を CSV 化

## Data Flow

### Step 1: 事前準備 (Android 側 tcpdump 確認)

```bash
# root 取得 + tcpdump 動作確認
adb -s 5HF6OVWCDECMJZ59 shell su -c 'tcpdump --version 2>&1 | head -1'
# 既存 tcpdump が無い場合:
adb -s 5HF6OVWCDECMJZ59 push ~/.nix-profile/bin/tcpdump /data/local/tmp/tcpdump
adb -s 5HF6OVWCDECMJZ59 shell su -c 'chmod 755 /data/local/tmp/tcpdump && \
  /data/local/tmp/tcpdump --version | head -1'
# IGMP join 確認
adb -s 5HF6OVWCDECMJZ59 shell su -c 'ip maddr show wlan0 | grep 239.255.0.1'
```

### Step 2: 計測ディレクトリ作成 + 両側 capture 起動

```bash
RUN_ID=$(date +%Y%m%d-%H%M%S)
RUN_DIR=artifacts/discovery-capture/$RUN_ID
mkdir -p $RUN_DIR/reliable-32 $RUN_DIR/best-effort-8192

# host tshark 起動
tshark -i wlan0 -f "udp portrange 7400-12500" \
  -w $RUN_DIR/host-reliable.pcap -b duration:300 \
  > $RUN_DIR/host-tshark.log 2>&1 &
HOST_PID=$!

# Android tcpdump 起動
adb -s 5HF6OVWCDECMJZ59 shell su -c \
  "/data/local/tmp/tcpdump -i wlan0 -n -B 4096 \
   'udp portrange 7400-12500' -w /sdcard/android-reliable.pcap -U" \
  > $RUN_DIR/android-tcpdump.log 2>&1 &
ANDROID_PID=$!

sleep 3
# クロック同期確認
echo $(date +%s.%N) > $RUN_DIR/host-clock-pre.txt
adb -s 5HF6OVWCDECMJZ59 shell 'date +%s.%N' > $RUN_DIR/android-clock-pre.txt
```

### Step 3: reliable-32 計測

```bash
dotnet run --project tools/rosettadds-perf-runner -c Release --no-build -- \
  --build-target Android \
  --scenario unity-to-ros2-reliable-32 \
  --android-device 5HF6OVWCDECMJZ59 \
  --artifacts $RUN_DIR/reliable-32 \
  --capture-frames 200
```

### Step 4: capture 停止 + pcap pull

```bash
# helper received 0/500 を確認 (想定通りなら B 仮説支持)
kill $ANDROID_PID 2>/dev/null
kill $HOST_PID 2>/dev/null
wait $ANDROID_PID $HOST_PID 2>/dev/null
adb -s 5HF6OVWCDECMJZ59 shell su -c 'cat /sdcard/android-reliable.pcap' \
  > $RUN_DIR/android-reliable.pcap
adb -s 5HF6OVWCDECMJZ59 shell su -c 'rm /sdcard/android-reliable.pcap'
```

### Step 5: best-effort-8192 計測 (control)

- Step 2-4 を `unity-to-ros2-best-effort-8192` で実施
- pcap ファイル名: `host-best-effort.pcap` / `android-best-effort.pcap`
- helper received 73-81% を確認 (想定通りなら SPDP/SEDP は届いている)

### Step 6: pcap 解析 (host 側 Python)

```python
# artifacts/discovery-capture/<runId>/analyze.py
# tshark で CSV 化 → pandas で集計
import subprocess, pandas as pd

def extract(path):
    out = subprocess.check_output([
        "tshark", "-r", path, "-Y", "rtps",
        "-T", "fields",
        "-e", "frame.time_epoch",
        "-e", "ip.src", "-e", "ip.dst",
        "-e", "udp.srcport", "-e", "udp.dstport",
        "-e", "rtps.sm.id",
    ], text=True)
    return pd.read_csv(StringIO(out), sep="\t",
        names=["ts","src","dst","sport","dport","sm_id"])

for name in ["host-reliable", "android-reliable",
             "host-best-effort", "android-best-effort"]:
    df = extract(f"{name}.pcap")
    df.to_csv(f"{name}.tsv", sep="\t", index=False)
    # port 別 count
    print(name, df.groupby("dport").size())
    # src IP 別 count
    print(name, df.groupby("src").size())
```

### Step 7: 仮説判定

各 pcap に対して以下を集計:

| 観点 | reliable-32 | best-effort-8192 | 解釈 |
|------|-------------|------------------|------|
| host が見た Android からの SPDP (src=192.168.0.22, dst port 7400) | n | n | Player が出していない → 仮説 1 支持 |
| Android 側 SPDP の送信 count | n | n | 同上 |
| host が見た Android からの SEDP (src=192.168.0.22, dst port 7410+2*PID) | n | n | SEDP が出ていない → 仮説 1 支持 |
| host の SPDP reply (src=192.168.0.20, dst=192.168.0.22) | n | n | helper は SPDP を見られている |
| host の SEDP reply | n | n | helper は SEDP を見られている |
| user data (dst port 7401) の host ↔ Android 間往復 | 0 (helper 0/500) | 73-81% | reliable だけ user data 経路が詰まる |

### Step 8: findings doc 作成

`docs/superpowers/specs/2026-06-27-discovery-capture-investigation.md` に:

- 計測日 / 環境 / branch
- 4 pcap の port 別 / src 別 / time window 別 packet count 表
- Step 7 の判定表 + 各仮説の支持 / 反証データ
- 根本原因 (判明した場合)
- next action (修正が必要なら別 PR scope、不可能なら limitation)
- 再現手順 (この doc 単独で他人が追試できる)

### Step 9: commit + PR

```bash
git add docs/superpowers/specs/2026-06-27-discovery-capture-investigation.md
git commit -m "docs(specs): Android reliable discovery 調査 (B) のパケットキャプチャ design を追加"
git push origin perf/discovery-capture-investigation
gh pr create --base main --head perf/discovery-capture-investigation \
  --title "docs(specs): Android reliable discovery 調査 (B) の design を追加" \
  --body "..."
```

## Error Handling

| 失敗ケース | 検出方法 | 対応 |
|------------|----------|------|
| Android `su` 不可 (root 取得失敗) | `su: not found` または permission denied | 計測前 Step 1 で必ず確認。root 不可なら本調査は不可能 → findings doc に limitation として明記、`tcpdump` の代替手段 (socat + BPF) または別端末検討 |
| Android tcpdump 静的バイナリ未 push | `tcpdump: not found` | `~/.nix-profile/bin/tcpdump` を push、permission 755。arm64 バイナリであることを確認 |
| tshark が wlan0 をキャプチャできない | `tshark: ... is not supported` | `-i any` で全インタフェース、または `wlan0mon` (monitor mode) に切替 |
| 計測 scenario が player crash | perf-runner manifest.json に `PlayerExitCode != 0` | capture 停止後、別 branch として記録、re-run 検討 |
| pcap pull 失敗 | `adb pull ... failed:Permission denied` | `/sdcard/` の permission 確認、`su -c 'chmod 666 /sdcard/android.pcap'` または `su -c 'cat'` で標準出力経由 pull |
| pcap サイズが巨大 (>100MB) | `ls -lh *.pcap` | filter を `udp portrange 7400-12500 and host 239.255.0.1` に絞る、ring buffer `-b filesize:51200 -b files:4` |
| host ↔ Android クロック同期誤差 >1s | Step 2 の `date +%s.%N` 差 | 計測前 NTP 同期試行 (`adb shell su -c 'ntpdate ...'`)、または `±1s window` 許容を findings に明記 |
| tshark の RTPS dissector が decode 失敗 | `rtps.sm.id` が空欄 | tshark の builtin endpoint entity id 解釈不可。`udp.port == 7400 || 7401` 等 port ベースに fallback |
| IGMP join 確認で `239.255.0.1` が無い | `ip maddr show wlan0` 結果に無し | helper (rmw_fastrtts_cpp) との discovery 不通の根本原因が Android kernel 側 IGMP 設定の可能性。`adb shell su -c 'ip maddr add 239.255.0.1 dev wlan0'` で手動 join 試行を findings で記録 |
| 仮説のどれも支持 / 反証データが得られない | 4 pcap 全てが packet 0 に近い | capture filter が狭すぎる可能性。`udp` 全体で再 capture、tshark の `rtps` dissector の代わりに port のみで判定 |

## Validation (完了条件)

- [ ] 4 pcap 取得: `host-reliable.pcap` / `android-reliable.pcap` / `host-best-effort.pcap` / `android-best-effort.pcap` がそれぞれ 1 KB 以上
- [ ] 4 ファイルそれぞれ `tshark -c 1 -r <file>` で読み込み可能 (破損なし)
- [ ] host-reliable.pcap に Android からの SPDP (src=192.168.0.22, dst port 7400) が 1 packet 以上含まれる
- [ ] android-reliable.pcap に Player からの SPDP (src=192.168.0.22, dst port 7400) が 1 packet 以上含まれる
- [ ] host-reliable.pcap に host (helper) からの SPDP reply (src=192.168.0.20, dst=192.168.0.22) が 1 packet 以上含まれる
- [ ] host-best-effort.pcap に Android からの user data packet (dst port 7401) が含まれる
- [ ] port 別 / src IP 別 packet count の表を findings に含める
- [ ] 3 つの仮説それぞれに支持 / 反証データを 1 段落以上つける
- [ ] 1 commit + 1 PR で findings doc のみを main にマージ可能
- [ ] `git status` clean、ブランチ `perf/discovery-capture-investigation` 上で作業

## Validation Gate (PR レビュー観点)

- **仮説の網羅性**: 「Packet 数 0」「Packet 数 100」「一部届く」 のどの結果でも、
  その意味を 1 段落で説明できている
- **切り分けの厳密性**: reliable と best-effort の差分がパケットキャプチャで
  説明できている (3 仮説以外の可能性は考慮不要なら明記)
- **next action の具体性**: 修正が必要なら別 PR の scope を 1-2 文で明記、
  不要なら「環境問題のため調査終了」と明示
- **再現性**: この doc 単独で第三者が追試できる (Step 1-9 のコマンドと
  期待される pcap 解析結果を含む)

## Steps (実行順)

1. ブランチ `perf/discovery-capture-investigation` を `main` から作成
2. `adb shell su -c 'tcpdump --version'` で root 取得 + tcpdump 動作確認
3. host / android で tcpdump 起動 (`artifacts/discovery-capture/<runId>/` 配下)
4. `unity-to-ros2-reliable-32` 計測 (約 30 秒)
5. キャプチャ停止、pcap pull
6. `unity-to-ros2-best-effort-8192` 計測 (約 30 秒)
7. キャプチャ停止、pcap pull
8. tshark + Python で 4 pcap を解析、port 別 / src 別 / time window 別 count 抽出
9. 3 仮説の支持 / 反証を整理
10. findings doc を `docs/superpowers/specs/2026-06-27-discovery-capture-investigation.md` に書く
11. 1 commit + 1 PR → main マージ

## Risks

- **root 端末でない**: 多くの Android 15 端末は userdebug でもない限り root 不可。
  Xperia 10 III のブートローダアンロック状況を事前確認。アンロック可能でも
  初期化 + セットアップに 30-60 分。
- **tcpdump の代替**: `su` で取得できない場合は `socat` + BPF の代替キャプチャを
  fallback として準備。ただし機能制限あり (RTPS 詳細 decode は不可)
- **wireshark GUI 不可**: 既に tshark + Python script で対応可能なため問題なし
- **マルチキャスト IGMP 設定**: Android 側で IGMP join が不完全な場合、
  `adb shell ip maddr show wlan0` で `239.255.0.1` が joined か事前確認。
  無い場合は本調査の findings で limitation として明示
- **時刻同期**: Android と host の RTC が大きく乖離している可能性。
  計測前に `adb shell date` で確認、必要なら NTP 同期
- **pcap 解析での RTPS decode 限界**: tshark の RTPS dissector は
  builtin endpoint (SPDP/SEDP) の entity id を完全には解釈できない場合あり。
  その場合 port ベースで判定 (Step 7 判定表)
- **ベストエフォート control の偶然性**: best-effort-8192 が 73-81% と
  部分的成功のため、control として 100% 信頼できるわけではない。
  best-effort-32 (helper received が 100% に近い) を追加 control として
  計測することも検討 (Step 5 で柔軟に対応)
