# ROSettaDDS

**Pure C#/.NET で実装した、ROS 2 互換 (DDS/RTPS) の通信ライブラリ。**

ネイティブライブラリもブリッジプロセスも必要とせず、ROS 2 ノードと直接 pub/sub できます。
DDS/RTPS のレイヤーから C# で実装しているため、Windows / macOS / Linux に加えて
Android / iOS など IL2CPP・AOT 環境でも動作するクロスプラットフォームを実現します。

[English](README.md) | 日本語

## なぜ ROSettaDDS か

Unity / .NET から ROS 2 と通信する手段は既にいくつか存在します。ROSettaDDS はそれらと
次の点で異なります。

|  | [ros-sharp](https://github.com/siemens/ros-sharp) | [ros2-for-unity](https://github.com/RobotecAI/ros2-for-unity) (ros2cs) | **ROSettaDDS** |
| --- | --- | --- | --- |
| 通信方式 | rosbridge (WebSocket / JSON) | ネイティブ `rcl`/`rmw` バインディング | Pure C# の RTPS/DDS |
| ブリッジプロセス | 必要 (`rosbridge_server`) | 不要 | 不要 |
| ネイティブ依存 | なし | あり (プラットフォーム毎にビルドが必要) | なし |
| 対応プラットフォーム | Unity/.NET が動く所 | ネイティブをビルド済みの環境 (Linux/Windows 中心) | **.NET が動く所** (Win/Mac/Linux/Android/iOS…) |
| ROS 2 と直接 pub/sub | ブリッジ経由 | 直接 | 直接 |
| オーバーヘッド | 大 (JSON シリアライズ + WebSocket) | 小 | 小 |
| AOT / IL2CPP | — | ネイティブ依存に従う | 対応 (msg はコンパイル時生成) |

- *ros-sharp** は ROS 2 側に `rosbridge_server` を立て、WebSocket 経由で JSON をやり取りする
  ブリッジ方式です。導入は容易ですが、ブリッジプロセスが別途必要で、JSON シリアライズと
  WebSocket のオーバーヘッドが乗ります。ROSettaDDS はブリッジを介さず、ROS 2 が話すのと同じ
  RTPS でネイティブに pub/sub します。
- **ros2-for-unity (ros2cs)** は思想が近く、ROS 2 の `rcl`/`rmw` ネイティブライブラリへ C# から
  バインドして直接 DDS pub/sub します。低オーバーヘッドですが、各プラットフォーム向けに
  ネイティブライブラリをビルド・同梱する必要があり、動作環境が限られます。ROSettaDDS は
  DDS/RTPS を C# だけで実装しているためネイティブ依存がなく、macOS やモバイルを含めて
  .NET が動く環境ならどこでも動かせます。

## 特徴

- ROS 2 (Fast DDS) と RTPS レベルで相互通信できる
- `std_msgs` / `builtin_interfaces` に対応。`.msg` から CDR 互換な C# 型を生成できる
- Reliable / Best Effort の QoS を選択できる
- ネイティブ依存ゼロ。IL2CPP / AOT 互換のコンパイル時 msg 生成
- Unity 6000.3 (.NET Standard 2.1) で検証済み。クロスプラットフォームビルドに対応

> [!NOTE]
> 互換性の対象範囲と検証方針は [docs/compatibility.md](docs/compatibility.md)、
> ROS 2 実装との相互運用確認は [docs/interop.md](docs/interop.md) を参照してください。

## クイックスタート

このリポジトリを clone した状態で、別の .NET コンソールアプリから `ROSettaDDS` を参照して試せます。

```sh
dotnet new console -n MyROSettaDDSApp
dotnet add MyROSettaDDSApp/MyROSettaDDSApp.csproj reference src/rosettadds/rosettadds.csproj
```

`Program.cs` を次の内容に置き換えると、`std_msgs/msg/String` の talker / listener として動作します。

```csharp
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Msgs.Std;

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
    EntityName = $"rosettadds_{mode}",
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
            var message = new StringMessage($"Hello rosettadds: {++count}");
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
dotnet run --project MyROSettaDDSApp -- listener
dotnet run --project MyROSettaDDSApp -- talker
```

listener 側に `I heard: 'Hello rosettadds: N'` が出れば送受信できています。
すぐ動かせるサンプルは [`samples/TalkerListener`](samples/TalkerListener) にあります。

## ROS 2 ノードと通信する

ローカル PC 内で ROS 2 と疎通する場合は、ROS 2 側も同じ domain と localhost 設定にします。

```sh
export ROS_DOMAIN_ID=0
export ROS_LOCALHOST_ONLY=1
```

ROS 2 の listener に rosettadds から送信する例:

```sh
ros2 run demo_nodes_cpp listener
dotnet run --project MyROSettaDDSApp -- talker
```

ROS 2 の talker を rosettadds で購読する例:

```sh
ros2 run demo_nodes_cpp talker
dotnet run --project MyROSettaDDSApp -- listener
```

別ホストと通信する場合は `ROS_LOCALHOST_ONLY` を無効にし、`LocalUnicastAddress` と
`MulticastInterface` に実際に使う NIC の IPv4 アドレスを指定してください。

## カスタムメッセージを生成する

`.msg` から CDR 互換な C# 型 (`struct` + `ICdrSerializer<T>`) をコンパイル時生成します
(IL2CPP / AOT 互換)。ランタイム生成は行いません。生成方法は 2 つあります。

### Source Generator (.NET プロジェクト向け、生成物のコミット不要)

`.msg` を `AdditionalFiles` に登録すると、ビルド時に透過的に C# 型が生成されます。

```xml
<ItemGroup>
  <ProjectReference Include="path/to/ROSettaDDS.SourceGenerator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>

<ItemGroup>
  <AdditionalFiles Include="msgs\**\*.msg" ROSettaDDSMsgPackage="sample_msgs" />
  <CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="ROSettaDDSMsgPackage" />
</ItemGroup>
```

例えば `msgs/sample_msgs/msg/Demo.msg` を

```
std_msgs/Header header
string name
float64[] values
int32 count
```

とすると、`ROSettaDDS.Msgs.Sample` 名前空間に `Demo` 型と `DemoSerializer` が生成され、
そのまま使えます。

```csharp
using ROSettaDDS.Msgs.Sample;

var demo = new Demo(header, "custom message", new[] { 1.5, 2.5, 3.5 }, 7);
```

動かせる実例は [`samples/CustomMsgGen`](samples/CustomMsgGen) を参照してください。

### CLI (標準 msg の保守、Unity への生成向け)

`<input>/<package>/msg/<Name>.msg` を走査して `.cs` を出力します。Unity の Assets 配下へ
生成する場合や、生成物をコミットして管理したい場合に使います。

```sh
# 標準 msg を再生成 (入力: msgs/, 出力: src/rosettadds/Msgs/)
dotnet run --project tools/rosettadds-genmsg -- --input msgs --output src/rosettadds/Msgs
```

> [!NOTE]
> 文法サポート範囲・命名ポリシー・各環境の使い方は [docs/msg-codegen.md](docs/msg-codegen.md) を参照してください。

## QoS を指定する

`CreatePublisher` は既定で Reliable publisher を作ります。ROS 2 の sensor-data 相当の
Best Effort subscriber へ送る場合は、publisher 作成時に QoS を明示します。

```csharp
using ROSettaDDS.Dds.QoS;

using var pub = participant.CreatePublisher<StringMessage>(
    "chatter",
    StringMessageSerializer.Instance,
    ReliabilityQos.BestEffort,
    StringMessage.DdsTypeName);
```

## 開発環境・ビルド

Nix flake で ROS 2 Humble + Fast DDS の RMW (`rmw_fastrtps_cpp`) + .NET 8 SDK が揃います。

```sh
# Nix flake の devShell に入る (direnv 利用時は自動 reload)
nix develop
```

devShell では `RMW_IMPLEMENTATION=rmw_fastrtps_cpp` を既定値にしています。
`ros.cachix.org` を利用するには `trusted-users` もしくは `trusted-substituters` に追加してください。
未設定だと ROS パッケージを自前ビルドすることになります。

```sh
dotnet build
dotnet test
```

## ドキュメント

| ドキュメント | 内容 |
| --- | --- |
| [docs/compatibility.md](docs/compatibility.md) | 互換性の対象範囲と検証方針 |
| [docs/interop.md](docs/interop.md) | ROS 2 実装との相互運用確認 |
| [docs/msg-codegen.md](docs/msg-codegen.md) | msg コード生成 (rosidl 相当) の文法・命名ポリシー |

サンプルは [`samples/`](samples) 配下にあります (`TalkerListener` / `CustomMsgGen` / `SpdpDemo`)。
