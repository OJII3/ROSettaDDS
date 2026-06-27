# 仮説判定 (2026-06-27)

| 観点 | reliable-32 host | best-effort-8192 host | 解釈 |
|------|------------------|------------------------|------|
| total RTPS packets | 603 | 1921 | best-effort が 3.2×多い (fragmentation) |
| port 12400 (SPDP mc) count | 14 | 17 | 両方とも host が SPDP 送信、Android が応答 |
| port 12401 (user mc) count | 1 | 1 | best-effort は multicast 送信あり |
| port 12410 (SEDP) count | 88 | 103 | 両方とも SEDP 双方向あり |
| port 12411 (user unicast) count | 500 | 1800 | reliable は unicast のみ (no reader proxy)、best-effort は fragmentation 多数 |
| src 192.168.0.20 (host) count | 92 | 100 | host の SPDP/SEDP 送信 |
| src 192.168.0.22 (Android) count | 511 | 1821 | Android の応答 + user data |
| helper received | 0/200 (0%) | 162/200 (81%) | 両者とも helper 側で 100% 到達せず |
| Android→host 到着率 | 511/500 (102%) | 1821/200 (910%) | 全 packet が host に到達 |

## 判定

### 仮説 1: Player 側 SPDP/SEDP 不出力 → **反証**
- Android (192.168.0.22) からの SPDP/SEDP 14+88 = 102 packet
- 2 scenarios とも Android は SPDP/SEDP を送信

### 仮説 2: Android kernel 経路問題 (IGMP / multicast routing) → **反証**
- Android からの全 multicast (port 12400, 12401) が host 側に到達
- IGMP は /proc/net/igmp 上で未確認だが、host から見ると到達している
- 仮説 2 の kernel multicast routing 自体は成立している

### 仮説 3: host 側 rmw_fastrtts_cpp parse 失敗 → **支持 (詳細不明)**
- reliable: 500 user data packet すべて host に到達、helper received 0
- best-effort: 1800 user data fragment すべて host に到達、helper received 162/200 (81%)
- host 側 rmw_fastrtts_cpp が parse 段階で失敗/欠落している

## 残存謎

1. **best-effort はなぜ 81% で止まるのか** (162/200 で残り 38 packet が欠落)
2. **reliable はなぜ 0% なのか** (500 packet すべて到達しているのに 1 件も処理されない)
3. **IGMP 未参加との関係**: Android kernel 上で wlan0 は 239.255.0.1 に未参加
   (/proc/net/igmp 確認)。host から見たマルチキャストは到達しているが、
   Android 側カーネルから見ると SPDP 受信経路が不明。
