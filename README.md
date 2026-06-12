# ROSettaDDS

**A ROS 2-compatible (DDS/RTPS) communication library implemented in pure C#/.NET.**

No native libraries and no bridge process required — talk directly to ROS 2 nodes via pub/sub.
Because the DDS/RTPS layer itself is implemented in C#, it runs **cross-platform**: Windows /
macOS / Linux as well as IL2CPP and AOT targets such as Android and iOS.

English | [日本語](README.ja.md)

## Why ROSettaDDS

Several ways already exist to talk to ROS 2 from Unity / .NET. ROSettaDDS differs from them as
follows.

|  | [ros-sharp](https://github.com/siemens/ros-sharp) | [ros2-for-unity](https://github.com/RobotecAI/ros2-for-unity) (ros2cs) | **ROSettaDDS** |
| --- | --- | --- | --- |
| Transport | rosbridge (WebSocket / JSON) | Native `rcl`/`rmw` bindings | Pure C# RTPS/DDS |
| Bridge process | **Required** (`rosbridge_server`) | Not required | Not required |
| Native dependency | None | **Yes** (must be built per platform) | None |
| Platforms | Anywhere Unity/.NET runs | Where the native libs are prebuilt (mainly Linux/Windows) | **Anywhere .NET runs** (Win/Mac/Linux/Android/iOS…) |
| Direct pub/sub with ROS 2 | Via bridge | Direct | Direct |
| Overhead | High (JSON serialization + WebSocket) | Low | Low |
| AOT / IL2CPP | — | Depends on native libs | Supported (msg generated at compile time) |

- **ros-sharp** is a bridge-based approach: you run `rosbridge_server` on the ROS 2 side and
  exchange JSON over WebSocket. It is easy to set up, but it requires a separate bridge process
  and incurs JSON serialization and WebSocket overhead. ROSettaDDS does pub/sub natively over the
  same RTPS that ROS 2 speaks, with no bridge in between.
- **ros2-for-unity (ros2cs)** shares a similar philosophy: it binds from C# to the native ROS 2
  `rcl`/`rmw` libraries and does DDS pub/sub directly. The overhead is low, but it requires
  building and shipping native libraries for each platform, which limits where it can run.
  ROSettaDDS implements DDS/RTPS **entirely in C#**, so it has no native dependency and runs
  anywhere .NET runs — including macOS and mobile.

## Features

- Interoperates with ROS 2 (Fast DDS) at the RTPS level
- Supports `std_msgs` / `builtin_interfaces`; generates CDR-compatible C# types from `.msg`
- Selectable Reliable / Best Effort QoS
- Zero native dependency; IL2CPP / AOT-compatible compile-time msg generation
- Verified on Unity 6000.3 (.NET Standard 2.1); supports cross-platform builds

> [!NOTE]
> See [docs/compatibility.md](docs/compatibility.md) for the supported scope and verification
> policy, and [docs/interop.md](docs/interop.md) for interoperability checks against ROS 2.

## Quick start

With this repository cloned, you can reference `ROSettaDDS` from a separate .NET console app.

```sh
dotnet new console -n MyROSettaDDSApp
dotnet add MyROSettaDDSApp/MyROSettaDDSApp.csproj reference src/rosettadds/rosettadds.csproj
```

Replace `Program.cs` with the following to run a `std_msgs/msg/String` talker / listener.

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

    // Settings to talk locally to a ROS 2 node started with ROS_LOCALHOST_ONLY=1.
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

Start the listener and talker in two shells.

```sh
dotnet run --project MyROSettaDDSApp -- listener
dotnet run --project MyROSettaDDSApp -- talker
```

If the listener prints `I heard: 'Hello rosettadds: N'`, messages are flowing.
A ready-to-run sample is in [`samples/TalkerListener`](samples/TalkerListener).

## Talking to ROS 2 nodes

To talk to ROS 2 on the same machine, use the same domain and localhost settings on the ROS 2
side as well.

```sh
export ROS_DOMAIN_ID=0
export ROS_LOCALHOST_ONLY=1
```

Send from rosettadds to a ROS 2 listener:

```sh
ros2 run demo_nodes_cpp listener
dotnet run --project MyROSettaDDSApp -- talker
```

Subscribe to a ROS 2 talker from rosettadds:

```sh
ros2 run demo_nodes_cpp talker
dotnet run --project MyROSettaDDSApp -- listener
```

To talk to another host, unset `ROS_LOCALHOST_ONLY` and set `LocalUnicastAddress` and
`MulticastInterface` to the IPv4 address of the NIC you actually use.

## Generating custom messages

CDR-compatible C# types (`struct` + `ICdrSerializer<T>`) are generated from `.msg` **at compile
time** (IL2CPP / AOT compatible); there is no runtime generation. There are two ways to do it.

### Source Generator (for .NET projects; no need to commit generated code)

Register `.msg` files as `AdditionalFiles` and the C# types are generated transparently at build
time.

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

For example, `msgs/sample_msgs/msg/Demo.msg`:

```
std_msgs/Header header
string name
float64[] values
int32 count
```

generates a `Demo` type and a `DemoSerializer` in the `ROSettaDDS.Msgs.Sample` namespace, ready
to use:

```csharp
using ROSettaDDS.Msgs.Sample;

var demo = new Demo(header, "custom message", new[] { 1.5, 2.5, 3.5 }, 7);
```

See [`samples/CustomMsgGen`](samples/CustomMsgGen) for a runnable example.

### CLI (for maintaining standard msgs / generating into Unity)

Scans `<input>/<package>/msg/<Name>.msg` and emits `.cs`. Use it to generate into a Unity Assets
folder, or when you want to commit and manage the generated code.

```sh
# Regenerate standard msgs (input: msgs/, output: src/rosettadds/Msgs/)
dotnet run --project tools/rosettadds-genmsg -- --input msgs --output src/rosettadds/Msgs
```

> [!NOTE]
> See [docs/msg-codegen.md](docs/msg-codegen.md) for the supported grammar, naming policy, and
> usage in each environment.

## Specifying QoS

`CreatePublisher` creates a Reliable publisher by default. To send to a Best Effort subscriber
(equivalent to ROS 2 sensor-data), specify the QoS explicitly when creating the publisher.

```csharp
using ROSettaDDS.Dds.QoS;

using var pub = participant.CreatePublisher<StringMessage>(
    "chatter",
    StringMessageSerializer.Instance,
    ReliabilityQos.BestEffort,
    StringMessage.DdsTypeName);
```

## Using it in Unity

In Unity, set the API Compatibility Level to **.NET Standard 2.1**. The currently verified editor
is **Unity 6000.3.7f1**. The Unity package declaration also targets Unity 6000.3, and
`src/rosettadds/csc.rsp` pins the C# language version to `10.0`.

To add another Unity version to the supported set, run package compile / EditMode test / PlayMode
test before treating it as verified.

## Development environment & build

A Nix flake provides ROS 2 Humble + the Fast DDS RMW (`rmw_fastrtps_cpp`) + the .NET 8 SDK.

```sh
# Enter the flake devShell (auto-reloads with direnv)
nix develop
```

The devShell sets `RMW_IMPLEMENTATION=rmw_fastrtps_cpp` by default. To use `ros.cachix.org`, add
it to `trusted-users` or `trusted-substituters`; otherwise ROS packages are built from source.

```sh
dotnet build
dotnet test
```

## Documentation

| Document | Contents |
| --- | --- |
| [docs/compatibility.md](docs/compatibility.md) | Supported scope and verification policy |
| [docs/interop.md](docs/interop.md) | Interoperability checks against ROS 2 |
| [docs/msg-codegen.md](docs/msg-codegen.md) | Grammar and naming policy of msg code generation (rosidl equivalent) |

Samples live under [`samples/`](samples) (`TalkerListener` / `CustomMsgGen` / `SpdpDemo`).
