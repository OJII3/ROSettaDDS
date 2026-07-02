// Legacy DomainParticipant API を使った互換性検証。
// ROSettaDDS.Rcl.Context/Node が正本だが、後方互換のため
// `ROSettaDDS.Dds.DomainParticipant` を直接利用する経路の挙動を
// ここで継続的にカバーする。
#pragma warning disable CS0618 // Type or member is obsolete (DomainParticipant)
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Tests.Integration;

public class PublisherBatchTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    private sealed class TestEnv
    {
        public required LoopbackHub Hub { get; init; }
        public required DomainParticipant ParticipantA { get; init; }
        public required DomainParticipant ParticipantB { get; init; }
    }

    private static TestEnv CreatePair(TimeSpan? writerHeartbeatPeriod = null)
    {
        var hub = new LoopbackHub();
        var multicastIp = IPAddress.Parse("239.255.0.1");
        var spdpLoc = Locator.FromUdpV4(multicastIp, 7400u);
        var userMcLoc = Locator.FromUdpV4(multicastIp, 7401u);

        var spdpA = hub.Create(spdpLoc);
        var spdpB = hub.Create(spdpLoc);
        var ucA = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7411u));
        var ucB = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7413u));
        var userMcA = hub.Create(userMcLoc);
        var userMcB = hub.Create(userMcLoc);
        var userUcA = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7412u));
        var userUcB = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7414u));

        var hbPeriod = writerHeartbeatPeriod ?? TimeSpan.FromSeconds(1);
        var optionsA = new DomainParticipantOptions
        {
            DomainId = 0, ParticipantId = 1, EntityName = "node_a",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            SedpInterval = TimeSpan.FromMilliseconds(50),
            UserWriterHeartbeatPeriod = hbPeriod,
            CustomMulticastTransport = spdpA,
            CustomUnicastTransport = ucA,
            CustomUserMulticastTransport = userMcA,
            CustomUserUnicastTransport = userUcA,
        };
        var optionsB = new DomainParticipantOptions
        {
            DomainId = 0, ParticipantId = 2, EntityName = "node_b",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            SedpInterval = TimeSpan.FromMilliseconds(50),
            UserWriterHeartbeatPeriod = hbPeriod,
            CustomMulticastTransport = spdpB,
            CustomUnicastTransport = ucB,
            CustomUserMulticastTransport = userMcB,
            CustomUserUnicastTransport = userUcB,
        };
        return new TestEnv
        {
            Hub = hub,
            ParticipantA = new DomainParticipant(optionsA),
            ParticipantB = new DomainParticipant(optionsB),
        };
    }

    [Fact]
    public async Task PublishManyAsync_は_全メッセージを_順序通り_受信できる()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var received = new List<int>();
        var lockObj = new object();
        using var sub = pB.CreateSubscription<StringMessage>(
            "batch_topic",
            StringMessageSerializer.Instance,
            (msg, _) =>
            {
                if (int.TryParse(msg.Data, out var v))
                {
                    lock (lockObj) { received.Add(v); }
                }
            },
            reliability: ReliabilityQos.Reliable);

        using var pub = pA.CreatePublisher<StringMessage>(
            "batch_topic",
            StringMessageSerializer.Instance,
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        const int N = 1000;
        var messages = new StringMessage[N];
        for (int i = 0; i < N; i++) messages[i] = new StringMessage(i.ToString());

        await pub.PublishManyAsync(messages);

        var deadline = DateTime.UtcNow + ReceiveTimeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (lockObj) { if (received.Count == N) break; }
            await Task.Delay(10);
        }
        lock (lockObj)
        {
            received.Should().HaveCount(N);
            received.Should().Equal(Enumerable.Range(0, N));
        }
    }

    [Fact]
    public async Task PublishRepeatedAsync_は_同じ値を_count_回_publish_できる()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var received = new List<string>();
        var lockObj = new object();
        using var sub = pB.CreateSubscription<StringMessage>(
            "repeat_topic",
            StringMessageSerializer.Instance,
            (msg, _) =>
            {
                lock (lockObj) { received.Add(msg.Data); }
            },
            reliability: ReliabilityQos.Reliable);

        using var pub = pA.CreatePublisher<StringMessage>(
            "repeat_topic",
            StringMessageSerializer.Instance,
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        const int N = 500;
        var msg = new StringMessage("payload");
        await pub.PublishRepeatedAsync(msg, N);

        var deadline = DateTime.UtcNow + ReceiveTimeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (lockObj) { if (received.Count == N) break; }
            await Task.Delay(10);
        }
        lock (lockObj)
        {
            received.Should().HaveCount(N);
            received.Should().AllSatisfy(s => s.Should().Be("payload"));
        }
    }

    [Fact]
    public async Task PublishManyAsync_は_count_0_で_何もしない()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var pub = pA.CreatePublisher<StringMessage>(
            "empty_topic",
            StringMessageSerializer.Instance,
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile);

        await pub.PublishManyAsync(Array.Empty<StringMessage>());
    }

    [Fact]
    public async Task PublishRepeatedAsync_は_count_0_で_何もしない()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var pub = pA.CreatePublisher<StringMessage>(
            "empty_repeat_topic",
            StringMessageSerializer.Instance,
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile);

        await pub.PublishRepeatedAsync(new StringMessage("x"), 0);
    }

    [Fact]
    public async Task PublishManyAsync_は_バッチサイズが_MaxSamples_を超えても_use_after_return_を起こさない()
    {
        // MaxSamples (既定 1000) を超える batch サイズで history evict が発生しても、
        // use-after-return (既に Dispose された PayloadOwner への参照) が起きないことを検証する。
        // 旧実装では全 Add 後に全 Send していたため evict で未送信の owner が Dispose され、
        // use-after-return を起こす可能性があった。
        // 修正後は Add → Send を交互に実行するため安全。
        var env = CreatePair(writerHeartbeatPeriod: TimeSpan.FromSeconds(10));
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var receivedLock = new object();
        var received = new List<string>();
        using var sub = pB.CreateSubscription<StringMessage>(
            "max_samples_batch_topic",
            StringMessageSerializer.Instance,
            (msg, _) =>
            {
                lock (receivedLock) { received.Add(msg.Data); }
            },
            reliability: ReliabilityQos.Reliable);

        using var pub = pA.CreatePublisher<StringMessage>(
            "max_samples_batch_topic",
            StringMessageSerializer.Instance,
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        const int N = 2000;
        var messages = new StringMessage[N];
        for (int i = 0; i < N; i++) messages[i] = new StringMessage(i.ToString());

        // use-after-return が起きるとここで ObjectDisposedException / データ破損が発生する
        await pub.PublishManyAsync(messages);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (receivedLock) { if (received.Count >= N) break; }
            await Task.Delay(10);
        }
        lock (receivedLock)
        {
            received.Should().HaveCount(N);
            for (int i = 0; i < N; i++)
            {
                received[i].Should().Be(i.ToString());
            }
        }
    }
}
