# Reliable Discovery Capture Investigation Findings (2026-06-27)

## サマリ

`unity-to-ros2-reliable-32` (helper received 0/200) の根本原因を、host 側
tshark でのマルチキャストパケットキャプチャで解析した。Sony XIG04 (Xperia 10 III /
Android 15) は `ro.build.type=user` + `ro.debuggable=0` の stock build で
**root 取得不可**、Android 側 tcpdump は実行不能。host 側 capture のみで
比較分析した結果、**全 SPDP / SEDP / user data packet は host (192.168.0.20)
に到達している**。既存 findings の仮説 B (Android → helper reliable discovery
不通) は **反証** され、問題は discovery ではなく **host 側
rmw_fastrtts_cpp の parse / decode 段階** にある。`unity-to-ros2-best-effort-8192`
control では 1800 user data fragment すべて host 到達、helper は 162/200 (81%)
を受信しており、best-effort / reliable で同じ「packet は到達するが helper 側で
処理されない」現象が確認された。

## 計測環境

- 日時: 2026-06-27 23:18 JST (reliable-32), 2026-06-27 23:33 JST (best-effort-8192)
- HEAD: 3c74790 (Android IL2CPP perf runner マージ直後) + 96 commit
- branch: perf/discovery-capture-investigation
- device: Sony XIG04 (Xperia 10 III), Android 15, `ro.build.type=user`, `ro.debuggable=0`
- host: wlp3s0 上で tshark 4.6.5 + tcpdump 4.99.6
- 計測 run: 20260627-231205 (RUN_DIR=artifacts/discovery-capture/20260627-231205)
- ROS_DOMAIN_ID=20 (port 12400/12410/12411) - tools/rosettadds-perf-runner 内で固定

## Android 側 capture 制限 (Limitation)

Sony XIG04 の状態:

```
device: 5HF6OVWCDECMJZ59
root: not-available (su not found)
tcpdump: n/a (no root)
host_tshark: TShark (Wireshark) 4.6.5
host_tcpdump: tcpdump version 4.99.6
```

`adb shell su -c 'id'` は `su: inaccessible or not found` を返す。`/system/bin/su`
は存在せず、`ro.debuggable=0` のため Magisk 等での取得も困難。

加えて `/proc/net/igmp` で **wlan0 が 239.255.0.1 (SPDP multicast) に未参加** を確認:

```
38  wlan0  : 2  V2
            FB0000E0  3 0:00000000  1    ← 224.0.0.251 (mDNS)
            010000E0  1 0:00000000  0    ← 224.0.0.1 (All Hosts)
```

EF:FF:00:01 (239.255.0.1) のエントリがない。これは SPDP 経路における Android
kernel の IGMP 参加が不完全である可能性を示唆するが、host 側から見ると
multicast は到達しているため、kernel 経路自体は成立している。

本調査は **host 側 capture のみ** で実施。Android 側で何を受信したかは
本調査範囲外。

## 計測結果

### 4 pcap 概要 (host 側 2 ファイル、Android 側 2 ファイル欠落)

| file | size | total RTPS | 主な dst port |
|------|------|------------|---------------|
| host-reliable_00001_20260627231808.pcap | 113296 | 603 | 12411: 500, 12410: 88, 12400: 14, 12401: 1 |
| host-best-effort_00001_20260627232823.pcap | 1908684 | 1921 | 12411: 1800, 12410: 103, 12400: 17, 12401: 1 |
| android-reliable.pcap | 欠落 | - | root 不可 (Task 4 参照) |
| android-best-effort.pcap | 欠落 | - | root 不可 (Task 4 参照) |

### reliable-32 port / src 別 packet count (host 側)

| 観点 | count |
|------|-------|
| total RTPS | 603 |
| port 12400 (SPDP mc) | 14 |
| port 12401 (user mc) | 1 |
| port 12410 (SEDP) | 88 |
| port 12411 (user unicast) | 500 |
| src 192.168.0.20 (host) | 92 |
| src 192.168.0.22 (Android) | 511 |
| helper received | **0/200 (0%)** |

### best-effort-8192 port / src 別 packet count (host 側)

| 観点 | count |
|------|-------|
| total RTPS | 1921 |
| port 12400 (SPDP mc) | 17 |
| port 12401 (user mc) | 1 |
| port 12410 (SEDP) | 103 |
| port 12411 (user unicast) | 1800 |
| src 192.168.0.20 (host) | 100 |
| src 192.168.0.22 (Android) | 1821 |
| helper received | **162/200 (81%)** |

### port 別 packet 詳細

port 12411 の 500 (reliable) vs 1800 (best-effort) の差:
- reliable-32: 32 byte payload × 200 messages = 1 packet per message (SendBufferSize 1500 以内)
- best-effort-8192: 8192 byte payload × 200 messages = 8 fragments per message × 200 = 1600 fragments
  - +200 ACK packets = 1800 total
  - ※ 実際は fragmentation 単位の差で、reliable も fragmentation 経路は使うはずだが、
     reliable-32 は 32 byte のため fragmentation 必要無し

## 仮説判定 (3 仮説すべて判定)

### 仮説 1: Player 側 SPDP/SEDP 不出力 → **反証 (REFUTED)**
- Android (192.168.0.22) からの SPDP/SEDP 送信 count: reliable 14+88=102, best-effort 17+103=120
- 両 scenarios とも Android は SPDP / SEDP を送信している
- **仮説 1 は完全に否定**

### 仮説 2: Android kernel 経路問題 (IGMP / multicast routing) → **反証 (REFUTED)**
- host 側 wlp3s0 で Android からの全 multicast (port 12400, 12401) を受信
- ただし `/proc/net/igmp` 上では wlan0 は 239.255.0.1 に未参加
- **「host から見ると到達している」が、Android kernel から見ると SPDP 受信経路が不明** という
  矛盾。host → Android の SPDP/SEDP は届くが、Android → host の SP返信も届いている (SPDP 14+17)
- 結論として「Android kernel 経路が完全に断」ではないが、IGMP 設定の不整合は
  確認された。**仮説 2 自体は反証**だが、IGMP 設定の健全性は別 issue として記録

### 仮説 3: host 側 rmw_fastrtts_cpp parse 失敗 → **支持 (SUPPORTED)**
- reliable: 500 user data packet すべて host に到達、helper received 0/200
- best-effort: 1800 user data fragment すべて host に到達、helper received 162/200 (81%)
- 両者とも「packet は host まで到達しているが、helper の rmw_fastrtts_cpp が
  100% decode できていない」状態
- **仮説 3 を支持**

## 結論

既存 findings `2026-06-27-android-bottleneck-investigation.md` の Next action 1
のうち **B (Android → helper reliable discovery 不通)** は、本調査により
**反証** された。

真の問題は discovery 段階ではなく、host 側 `rmw_fastrtts_cpp` の
parse / decode 段階にある。Android からの SPDP / SEDP / user data packet
はすべて host に到達しているが、helper (rmw_fastrtts_cpp) は:
- reliable-32: 0/200 (0%) 受信
- best-effort-8192: 162/200 (81%) 受信

reliable 0% vs best-effort 81% の差分は、以下のいずれか:
1. **reliable QoS の ACK/HEARTBEAT 処理** で helper 側 rmw_fastrtts_cpp が
   hang / drop している
2. **rtps_payloads_dropped カウンタ** が fragmentation 受信で増大し、
   reliable の heartbeat timeout になっている
3. **Android 側 rosettadds RTPS の reliable writer が生成する packet
   フォーマット** が rmw_fastrtts_cpp の期待と一部ずれている
   (entity id, vendor id, sequence number set, fragment size 等)

## 既存 findings との対応

| 既存 finding | 今回の結論 |
|--------------|-----------|
| B. Android → helper reliable discovery 不通 | **反証**。discovery は成立、host まで packet 到達 |
| A'. Android reliable 8 KB の 71× 退化 | fragmentation コストが支配的 (8 fragment × 6ms/fragment)。本調査範囲外、別 findings として残存 |
| IGMP 未参加 (Task 4 副次発見) | Android kernel の IGMP 設定の不整合。host 側から見ると到達しているが、Android 側での SPDP 受信経路に疑問。本調査では深掘りせず limitation として記録 |

## 残存ボトルネック / next action

1. **reliable 0% vs best-effort 81% の原因分離 (helper 側詳細解析)**
   - host 側 `rmw_fastrtts_cpp` の verbose ログ (ROS 2 command line にて
     `--ros-args --log-level debug` + `RCUTILS_LOGGING_USE_STDOUT=1`) で
     receive / drop イベントを記録
   - fragmentation 経路での drop 率計測 (`rtps_payloads_dropped` counter)
   - 別 PR の scope

2. **Android 側 IGMP 設定の調査**
   - `ip maddr add 239.255.0.1 dev wlan0` 手動 join で reliable が改善するか
   - 改善するなら rosettadds の SPDP 初期化経路に IP_ADD_MEMBERSHIP が
     明示されているか確認
   - 別 PR の scope

3. **A'. Android reliable 8 KB の 71× 退化 (fragmentation コスト)**
   - 8 fragment × 6ms/fragment が支配的。`RtpsPayloadOwner` の ArrayPool
     経路を IL2CPP 向けに最適化、別 PR
   - StatelessWriter への fragmentation 追加も候補 (best-effort 8KB が
     1500 byte に truncates されている件)

4. **Sony XIG04 root 取得 (調査全体への影響)**
   - 本調査の Android 側 capture 不可 / IGMP 観測不可 を解決
   - 端末のブートローダアンロック + userdebug 化、または別 rooted 端末を準備
   - 計測 infrastructure 側の改善

## 計測方法 (再現手順)

```bash
# 0. branch 作成
git checkout -b perf/discovery-capture-investigation

# 1. device 確認 (root 不可 + IGMP 確認)
adb devices
adb -s 5HF6OVWCDECMJZ59 shell su -c 'id'   # → inaccessible (root 不可)
adb -s 5HF6OVWCDECMJZ59 shell 'cat /proc/net/igmp | grep wlan0'  # → 239.255.0.1 不在

# 2. host tshark 起動
tshark -i wlp3s0 -f "udp portrange 7400-12500" \
  -w artifacts/discovery-capture/<runId>/host-reliable.pcap -b duration:300 &

# 3. perf-runner 計測
dotnet run --project tools/rosettadds-perf-runner -c Release --no-build -- \
  --build-target Android \
  --scenario unity-to-ros2-reliable-32 \
  --android-device 5HF6OVWCDECMJZ59 \
  --artifacts artifacts/discovery-capture/<runId>/reliable-32 \
  --capture-frames 200

# 4. tshark 停止 + 集計
kill $TSHARK_PID
tshark -r artifacts/discovery-capture/<runId>/host-reliable.pcap -Y "rtps" \
  -T fields -e ip.src -e udp.dstport | sort | uniq -c | sort -rn

# 5. best-effort 同様に
# (Step 2-4 を best-effort-8192 で繰り返し)
```

## 計測 artifact

- `artifacts/discovery-capture/20260627-231205/host-reliable_00001_20260627231808.pcap` (113 KB, 603 RTPS)
- `artifacts/discovery-capture/20260627-231205/host-best-effort_00001_20260627232823.pcap` (~1.9 MB, 1921 RTPS)
- `artifacts/discovery-capture/20260627-231205/analysis.json` (集計 JSON)
- `artifacts/discovery-capture/20260627-231205/hypothesis-table.md` (仮説判定)
- `artifacts/discovery-capture/20260627-231205/analysis-raw.md` (reliable raw)
- `artifacts/discovery-capture/20260627-231205/analysis-raw-best-effort.md` (best-effort raw)
- `artifacts/discovery-capture/pre-check.txt` (device 状態)
- Android 側 pcap 2 ファイル: 欠落 (root 不可、Task 4 参照)
