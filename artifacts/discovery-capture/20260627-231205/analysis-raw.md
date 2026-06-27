# discovery-capture reliable-32 集計 raw データ

## 計測メタ

- pcap: `host-reliable_00001_20260627231808.pcap`
- 計測日時: 2026-06-27 23:18:08
- android IP: 192.168.0.22
- host IP: 192.168.0.20

## 集計結果

| 指標 | 値 |
|------|-----|
| Total packets (pcap 全パケット) | 607 |
| RTPS packets (rtps フィルタ) | 603 |
| RTPS, android→host (src 192.168.0.22) | 511 |
| RTPS, host→android (src 192.168.0.20) | 92 |

合計 603 (511+92) が RTPS フィルタの 603 と一致する。

### Android src (192.168.0.22) → dport 分布

| dport | パケット数 | 備考 |
|-------|-----------|------|
| 12411 | 500 | SPDP ユニキャスト応答 |
| 12410 | 8 | SEDP 応答 |
| 12400 | 2 | SPDP マルチキャスト |
| 12401 | 1 | SEDP マルチキャスト |

### Host src (192.168.0.20) → dport 分布

| dport | パケット数 | 備考 |
|-------|-----------|------|
| 12410 | 80 | SEDP 要求 |
| 12400 | 12 | SPDP アナウンス |
