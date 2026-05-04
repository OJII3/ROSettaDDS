# rclsharp

ROS 2 と互換な通信ライブラリ

- DDS と最低限の互換性がある
- std_msgs が使える
- Unity で使える
- Unity でクロスプラットフォームビルドできる (Windows, Mac, Android, ...)

## 開発環境

Nix flake で ROS 2 Humble + Fast DDS の RMW (`rmw_fastrtps_cpp`) + .NET 8 SDK が揃います。

```sh
# Nix flake の devShell に入る (direnv 利用時は自動 reload)
nix develop
```

devShell では `RMW_IMPLEMENTATION=rmw_fastrtps_cpp` を既定値にしています。

`ros.cachix.org` を利用するには `trusted-users` もしくは `trusted-substituters` に追加してください。未設定だと ROS パッケージを自前ビルドすることになります。

## ビルド・テスト

```sh
dotnet build
dotnet test
```

## Unity 互換性

Unity では API Compatibility Level を .NET Standard 2.1 に設定して利用します。
Unity 2022.3 の C# コンパイラは C# 11 の `required` members に対応していないため、
ライブラリ本体では `required` を使わず、Unity 同梱の .NET Standard 2.1 参照アセンブリで
コンパイルできる API に留めます。

## 終了時の discovery lifecycle

Participant は DDSI-RTPS の lease timeout で remote から消えることを前提にします。
Publisher / Subscription の endpoint は Dispose 時に SEDP の built-in Topic へ
`PID_STATUS_INFO` 付き unregister DATA を送信し、remote の graph から早めに消えるようにします。

## サンプル: SPDP Demo

指定ドメインの SPDP マルチキャストに参加し、他の Participant (rclsharp 同士 / ROS 2 ノード) を検出します。

```sh
# Usage: dotnet run --project samples/SpdpDemo -- [domainId] [participantId] [entityName]
dotnet run --project samples/SpdpDemo -- 0 1 rclsharp_demo
```

## サンプル: Talker / Listener

`/chatter` トピック (std_msgs/String) で文字列を送受信します。別シェルで起動してください。

```sh
# listener
dotnet run --project samples/TalkerListener -- listener
# talker
dotnet run --project samples/TalkerListener -- talker
```

listener 側に `I heard: 'Hello rclsharp: N'` が出れば OK。

## ROS 2 との相互検出確認 (loopback)

別シェルで ROS 2 talker を起動:

```sh
ROS_LOCALHOST_ONLY=1 ROS_DOMAIN_ID=0 ros2 run demo_nodes_cpp talker
```

SpdpDemo 側のログに `++ DISCOVERED ... unicast=UDPv4://127.0.0.1:7410` が出れば OK。
