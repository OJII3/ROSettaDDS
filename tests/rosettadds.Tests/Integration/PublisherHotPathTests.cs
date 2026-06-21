using System.Diagnostics;
using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Tests.Integration;

public class PublisherHotPathTests
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
    public async Task Reliable_small_payload_を_1000件_publish_すると全件_順序通り_受信できる()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var received = new List<int>();
        var lockObj = new object();
        using var sub = pB.CreateSubscription<StringMessage>(
            "perf_topic",
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
            "perf_topic",
            StringMessageSerializer.Instance,
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        const int N = 1000;
        for (int i = 0; i < N; i++)
        {
            await pub.PublishAsync(new StringMessage(i.ToString()));
        }

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
    public async Task BestEffort_large_payload_でも_publish_が_完了する()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var receivedTcs = new TaskCompletionSource<StringMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = pB.CreateSubscription<StringMessage>(
            "large_topic",
            StringMessageSerializer.Instance,
            (msg, _) => receivedTcs.TrySetResult(msg),
            reliability: ReliabilityQos.BestEffort);

        using var pub = pA.CreatePublisher<StringMessage>(
            "large_topic",
            StringMessageSerializer.Instance,
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        var big = new StringMessage(new string('x', 8192));
        await pub.PublishAsync(big);

        var received = await receivedTcs.Task.WaitAsync(ReceiveTimeout);
        received.Data.Length.Should().Be(8192);
    }

    [Fact]
    public async Task Publish_1件あたりの_GC_allocation_が_過剰でない()
    {
        var env = CreatePair(writerHeartbeatPeriod: TimeSpan.FromSeconds(10));
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var sub = pB.CreateSubscription<StringMessage>(
            "alloc_topic",
            StringMessageSerializer.Instance,
            (_, _) => { },
            reliability: ReliabilityQos.Reliable);

        using var pub = pA.CreatePublisher<StringMessage>(
            "alloc_topic",
            StringMessageSerializer.Instance,
            reliability: ReliabilityQos.Reliable,
            durability: DurabilityQos.Volatile);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        var msg = new StringMessage("hello");

        for (int i = 0; i < 100; i++)
        {
            await pub.PublishAsync(msg);
            await Task.Delay(1);
        }

        await Task.Delay(200);

        // Drain pending allocations from warmup
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        const int N = 500;
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < N; i++)
        {
            await pub.PublishAsync(msg);
        }
        long after = GC.GetTotalAllocatedBytes(precise: true);

        double perPublish = (after - before) / (double)N;
        // 環境依存の allocation を吸収するため 2 KB を閾値とする。
        // LoopbackTransport の packet.ToArray() が受信側で allocation を発生させるため、
        // 完璧な 0 にはならない。
        perPublish.Should().BeLessThan(2048,
            $"1 publish あたりの allocation が想定外: {perPublish:F1} bytes");
    }

    [Fact]
    public async Task Reliable_small_payload_の_LoopbackHub_スループットが_ベースラインを満たす()
    {
        var env = CreatePair(writerHeartbeatPeriod: TimeSpan.FromSeconds(10));
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        int received = 0;
        using var sub = pB.CreateSubscription<StringMessage>(
            "tput_topic",
            StringMessageSerializer.Instance,
            (_, _) => Interlocked.Increment(ref received),
            reliability: ReliabilityQos.Reliable);

        using var pub = pA.CreatePublisher<StringMessage>(
            "tput_topic",
            StringMessageSerializer.Instance,
            reliability: ReliabilityQos.Reliable,
            durability: DurabilityQos.Volatile);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        const int N = 1000;
        var msg = new StringMessage("payload");

        for (int i = 0; i < 50; i++)
        {
            await pub.PublishAsync(msg);
            await Task.Delay(1);
        }
        await Task.Delay(100);
        Volatile.Write(ref received, 0);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++)
        {
            await pub.PublishAsync(msg);
        }
        sw.Stop();

        // 全メッセージが配信されるまで待機
        var deliveryDeadline = DateTime.UtcNow + ReceiveTimeout;
        while (Volatile.Read(ref received) < N && DateTime.UtcNow < deliveryDeadline)
        {
            await Task.Delay(10);
        }
        Volatile.Read(ref received).Should().Be(N,
            "全メッセージが配信されるべき");

        double tps = N / Math.Max(0.000001, sw.Elapsed.TotalSeconds);
        tps.Should().BeGreaterThan(10_000,
            $"LoopbackHub throughput {tps:F0} msg/s が 10,000 msg/s 未満");
    }
}
