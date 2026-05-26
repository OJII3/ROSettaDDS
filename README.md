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

互換性の対象範囲と検証方針は [docs/compatibility.md](docs/compatibility.md) にまとめています。
ROS 2 実装との相互運用確認は [docs/interop.md](docs/interop.md) を参照してください。

## 使い方

### 新規コンソールアプリから使う

このリポジトリを clone した状態で、別の .NET アプリから `rclsharp` を参照して試せます。

```sh
dotnet new console -n MyRclsharpApp
dotnet add MyRclsharpApp/MyRclsharpApp.csproj reference src/rclsharp/rclsharp.csproj
```

`Program.cs` を次の内容に置き換えると、`std_msgs/msg/String` の talker / listener として動作します。

```csharp
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Rclsharp.Common.Logging;
using Rclsharp.Dds;
using Rclsharp.Msgs.Std;

if (args.Length != 1 || (args[0] != "talker" && args[0] != "listener"))
{
    Console.Error.WriteLine("Usage: dotnet run -- <talker|listener>");
    return;
}

var mode = args[0];
var logger = new ConsoleLogger(mode, LogLevel.Info);

var options = new DomainParticipantOptions
{
    DomainId = 0,
    ParticipantId = mode == "talker" ? 1 : 2,
    EntityName = $"rclsharp_{mode}",
    Logger = logger,

    // ROS_LOCALHOST_ONLY=1 の ROS 2 ノードとローカルで通信する設定。
    LocalUnicastAddress = IPAddress.Loopback,
    MulticastInterface = IPAddress.Loopback,
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var participant = new DomainParticipant(options);
participant.Start();

try
{
    if (mode == "talker")
    {
        using var pub = participant.CreatePublisher<StringMessage>(
            "chatter",
            StringMessageSerializer.Instance,
            StringMessage.DdsTypeName);

        var count = 0;
        while (!cts.IsCancellationRequested)
        {
            var message = new StringMessage($"Hello rclsharp: {++count}");
            await pub.PublishAsync(message, cts.Token);
            logger.Info($"Publishing: '{message.Data}'");
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
        }
    }
    else
    {
        using var sub = participant.CreateSubscription<StringMessage>(
            "chatter",
            StringMessageSerializer.Instance,
            (message, source) => logger.Info($"I heard: '{message.Data}' from {source}"),
            StringMessage.DdsTypeName);

        await Task.Delay(Timeout.Infinite, cts.Token);
    }
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    logger.Info("Stopping...");
}
```

2 つのシェルで listener と talker を起動します。

```sh
dotnet run --project MyRclsharpApp -- listener
dotnet run --project MyRclsharpApp -- talker
```

listener 側に `I heard: 'Hello rclsharp: N'` が出れば送受信できています。

### ROS 2 ノードと通信する

ローカル PC 内で ROS 2 と疎通する場合は、ROS 2 側も同じ domain と localhost 設定にします。

```sh
export ROS_DOMAIN_ID=0
export ROS_LOCALHOST_ONLY=1
```

ROS 2 の listener に rclsharp から送信する例:

```sh
ros2 run demo_nodes_cpp listener
dotnet run --project MyRclsharpApp -- talker
```

ROS 2 の talker を rclsharp で購読する例:

```sh
ros2 run demo_nodes_cpp talker
dotnet run --project MyRclsharpApp -- listener
```

別ホストと通信する場合は `ROS_LOCALHOST_ONLY` を無効にし、`LocalUnicastAddress` と
`MulticastInterface` に実際に使う NIC の IPv4 アドレスを指定してください。

### QoS を指定して publish する

`CreatePublisher` は既定で Reliable publisher を作ります。ROS 2 の sensor-data 相当の
Best Effort subscriber へ送る場合は、publisher 作成時に QoS を明示します。

```csharp
using Rclsharp.Dds.QoS;

using var pub = participant.CreatePublisher<StringMessage>(
    "chatter",
    StringMessageSerializer.Instance,
    ReliabilityQos.BestEffort,
    StringMessage.DdsTypeName);
```

## Unity 互換性

Unity では API Compatibility Level を .NET Standard 2.1 に設定して利用します。
現在の検証済み Editor は Unity 6000.3.7f1 です。Unity package 宣言も Unity 6000.3 に合わせ、
`src/rclsharp/csc.rsp` では C# language version を `10.0` に固定しています。
別 Unity バージョンを対応対象に加える場合は、package compile、EditMode test、PlayMode test を通してから
検証済み範囲として扱います。

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

<!-- rclsharp-local-performance:start -->
## ローカル性能計測結果

Unity EditMode のローカル計測結果です。実行環境や同時負荷で変動します。

- 計測日時: 2026-05-26 15:49:54 UTC
- Unity: 6000.3.7f1 (OSXEditor, Mono2x)
- 実行環境: Mac OS X 26.5.0, Apple M2, 24,576 MB RAM

### Throughput

| Payload | Median messages/sec | Median serialized MiB/sec | Median mean ms/message | Samples |
| --- | ---: | ---: | ---: | ---: |
| 32 B | 31,026 | 1.2131 | 0.0322 | 5 |
| 1024 B | 39,151 | 38.57 | 0.0255 | 5 |
| 8192 B | 9,168 | 71.71 | 0.1091 | 5 |

### Leak Guard

| Metric | Final retained | Max retained |
| --- | ---: | ---: |
| Managed heap | 0 B | 0 B |
| Unity mono used | 0 B | 0 B |

<!-- rclsharp-local-performance:end -->
