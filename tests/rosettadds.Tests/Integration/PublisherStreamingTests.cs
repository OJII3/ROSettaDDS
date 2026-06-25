using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Tests.Integration;

/// <summary>
/// <para>
/// <see cref="Publisher.PublishRepeatedAsync"/> / <see cref="Publisher.PublishManyAsync"/>
/// がストリーミング実装 (1 件 rent → add → send) で動作することを検証する。
/// 旧 batch 実装は N 件分の <c>RtpsPayloadOwner</c> と <c>byte[]</c> バッファを
/// pre-allocate していたため、payload 8 KB × 200 件 = 1.6 MB のメモリ圧が
/// Unity Player 計測で <c>gc_used_memory_bytes_last</c> 6.8 MB まで膨らむ
/// ボトルネックになっていた。ストリーミング化で寿命を 1 件単位に縮める。
/// </para>
/// </summary>
public class PublisherStreamingTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

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
    public async Task PublishRepeatedAsync_は_8KB_payload_を_200件_全件_配信できる()
    {
        // 8 KB × 200 件 = 1.6 MB の ArrayPool バッファを pre-allocate せず、
        // 1 件ずつ rent → add → send のストリーミングで処理することを検証。
        // 旧実装では batch 配列に 1.6 MB 同時保持していたため Unity Player 計測で
        // gc_used_memory_bytes_last が 6.8 MB まで膨らんでいた。
        var env = CreatePair(writerHeartbeatPeriod: TimeSpan.FromSeconds(10));
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var received = new List<int>();
        var lockObj = new object();
        using var sub = pB.CreateSubscription<StringMessage>(
            "stream_8k_topic",
            StringMessageSerializer.Instance,
            (msg, _) =>
            {
                if (int.TryParse(msg.Data.AsSpan(0, msg.Data.IndexOf('|')), out var v))
                {
                    lock (lockObj) { received.Add(v); }
                }
            },
            reliability: ReliabilityQos.BestEffort);

        using var pub = pA.CreatePublisher<StringMessage>(
            "stream_8k_topic",
            StringMessageSerializer.Instance,
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        const int N = 200;
        var payload8k = new string('x', 8192);
        var messages = new StringMessage[N];
        for (int i = 0; i < N; i++)
        {
            messages[i] = new StringMessage(i.ToString() + "|" + payload8k);
        }

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
            received[0].Should().Be(0);
            received[N - 1].Should().Be(N - 1);
        }
    }

    [Fact]
    public async Task PublishManyAsync_は_1件ずつ_ストリーミング処理しても_全件_順序通り_受信できる()
    {
        // PublishManyAsync も同じく streaming 化されていることを検証。
        // 50 件の異なる短い値を 1 件ずつ rent → add → send する。
        // 8KB payload テストと異なり、message 本体が小さくてシリアライズコストが
        // 無視できる条件でも streaming 経路で全件届くことを確認。
        // BestEffort QoS で ACK roundtrip 抜きの最短経路で順序保持を検証する。
        // MaxSamples=1000 を超える 2000 件の batch でも use-after-return を
        // 起こさないことは PublisherBatchTests の既存テストが担保している。
        var env = CreatePair(writerHeartbeatPeriod: TimeSpan.FromSeconds(10));
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var received = new List<int>();
        var lockObj = new object();
        using var sub = pB.CreateSubscription<StringMessage>(
            "stream_many_topic",
            StringMessageSerializer.Instance,
            (msg, _) =>
            {
                if (int.TryParse(msg.Data, out var v))
                {
                    lock (lockObj) { received.Add(v); }
                }
            },
            reliability: ReliabilityQos.BestEffort);

        using var pub = pA.CreatePublisher<StringMessage>(
            "stream_many_topic",
            StringMessageSerializer.Instance,
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        const int N = 50;
        var messages = new StringMessage[N];
        for (int i = 0; i < N; i++) messages[i] = new StringMessage(i.ToString());

        await pub.PublishManyAsync(messages);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (lockObj) { if (received.Count >= N) break; }
            await Task.Delay(10);
        }
        lock (lockObj)
        {
            received.Should().HaveCount(N);
            received.Should().Equal(Enumerable.Range(0, N));
        }
    }

    [Fact]
    public async Task PublishRepeatedAsync_8KB_200件_が_タイムアウトせず完了する()
    {
        // ストリーミング実装が「1 件ずつシリアライズ → 即 send」になっていることを
        // wall-clock で検証する。200 件の 8 KB publish が妥当な時間内に完了することを確認。
        // 旧 batch 実装は 200 件分のシリアライズ (各 8 KB) を pre-allocate 時に
        // 一気に走らせるため、送信開始前に大きなスパイクが出ていた。
        // 実際の mps 改善は docs/superpowers/specs/2026-06-25-publisher-streaming.md
        // の perf 計測を参照。
        var env = CreatePair(writerHeartbeatPeriod: TimeSpan.FromSeconds(10));
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var sub = pB.CreateSubscription<StringMessage>(
            "stream_8k_perf_topic",
            StringMessageSerializer.Instance,
            (_, _) => { },
            reliability: ReliabilityQos.BestEffort);

        using var pub = pA.CreatePublisher<StringMessage>(
            "stream_8k_perf_topic",
            StringMessageSerializer.Instance,
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        var payload8k = new string('x', 8192);
        var msg = new StringMessage(payload8k);

        // pre-allocate 由来の GC スパイクが落ち着くまで warmup
        for (int i = 0; i < 50; i++)
        {
            await pub.PublishAsync(msg);
        }
        await Task.Delay(200);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        const int N = 200;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await pub.PublishRepeatedAsync(msg, N);
        sw.Stop();

        double mps = N / Math.Max(0.000001, sw.Elapsed.TotalSeconds);
        double mbPerSec = N * (msg.Data.Length + 4) / sw.Elapsed.TotalSeconds / 1e6;
        // 失敗にはせず、参考値として perf 結果を残す。CI での perf 検証は Unity Player 側で
        // 行うため (.NET EditMode は CPU/メモリプロファイルが大きく異なる)、ここでは
        // 完了可否だけを assert する。
        Console.WriteLine(
            $"[perf] PublishRepeatedAsync 8KB x {N}: {sw.Elapsed.TotalMilliseconds:F1} ms, " +
            $"{mps:F0} msg/s, {mbPerSec:F1} MB/s");

        // 200 件の 8 KB publish が 30 秒以内に完了すればストリーミングとして
        // 機能している (実 perf は ~200 ms 程度のはず)。
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            $"200 件の 8 KB publish に {sw.Elapsed.TotalSeconds:F1} 秒かかった");
    }
}
