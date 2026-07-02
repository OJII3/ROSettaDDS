// Legacy DomainParticipant API を使った互換性検証。
// ROSettaDDS.Rcl.Context/Node が正本だが、後方互換のため
// `ROSettaDDS.Dds.DomainParticipant` を直接利用する経路の挙動を
// ここで継続的にカバーする。
#pragma warning disable CS0618 // Type or member is obsolete (DomainParticipant)
using System.Net;
using ROSettaDDS.Cdr;
using ROSettaDDS.Cdr.ParameterList;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Integration;

public class ServiceClientLoopbackTests
{
    // --- インライン型定義 ---

    private readonly struct AddTwoIntsRequest
    {
        public long A { get; }
        public long B { get; }

        public AddTwoIntsRequest(long a, long b)
        {
            A = a;
            B = b;
        }
    }

    private sealed class AddTwoIntsRequestSerializer : ICdrSerializer<AddTwoIntsRequest>
    {
        public static readonly AddTwoIntsRequestSerializer Instance = new();

        public bool IsKeyed => false;

        public int GetSerializedSize(in AddTwoIntsRequest value) => 16;

        public void Serialize(ref CdrWriter writer, in AddTwoIntsRequest value)
        {
            writer.WriteInt64(value.A);
            writer.WriteInt64(value.B);
        }

        public void Deserialize(ref CdrReader reader, out AddTwoIntsRequest value)
            => value = new AddTwoIntsRequest(reader.ReadInt64(), reader.ReadInt64());

        public void SerializeKey(ref CdrWriter writer, in AddTwoIntsRequest value) { }
    }

    private readonly struct AddTwoIntsResponse
    {
        public long Sum { get; }

        public AddTwoIntsResponse(long sum)
        {
            Sum = sum;
        }
    }

    private sealed class AddTwoIntsResponseSerializer : ICdrSerializer<AddTwoIntsResponse>
    {
        public static readonly AddTwoIntsResponseSerializer Instance = new();

        public bool IsKeyed => false;

        public int GetSerializedSize(in AddTwoIntsResponse value) => 8;

        public void Serialize(ref CdrWriter writer, in AddTwoIntsResponse value)
            => writer.WriteInt64(value.Sum);

        public void Deserialize(ref CdrReader reader, out AddTwoIntsResponse value)
            => value = new AddTwoIntsResponse(reader.ReadInt64());

        public void SerializeKey(ref CdrWriter writer, in AddTwoIntsResponse value) { }
    }

    // --- LoopbackHub 用シングル参加者ファクトリ ---

    private static DomainParticipant CreateSingleParticipant(out LoopbackHub hub)
    {
        hub = new LoopbackHub();
        var multicastIp = IPAddress.Parse("239.255.0.1");
        var spdpLoc = Locator.FromUdpV4(multicastIp, 7400u);
        var userMcLoc = Locator.FromUdpV4(multicastIp, 7401u);

        var spdp = hub.Create(spdpLoc);
        var uc = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7411u));
        var userMc = hub.Create(userMcLoc);
        var userUc = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7412u));

        var options = new DomainParticipantOptions
        {
            DomainId = 0, ParticipantId = 1, EntityName = "svc_client_test",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            SedpInterval = TimeSpan.FromMilliseconds(50),
            CustomMulticastTransport = spdp,
            CustomUnicastTransport = uc,
            CustomUserMulticastTransport = userMc,
            CustomUserUnicastTransport = userUc,
        };

        return new DomainParticipant(options);
    }

    // --- reply ペイロードビルダ ---

    private static byte[] BuildReplyPayload(AddTwoIntsResponse response)
    {
        var buffer = new byte[CdrEncapsulation.Size + 32];
        CdrEncapsulation.Write(buffer, CdrEncapsulation.CdrLittleEndian);
        var w = new CdrWriter(buffer, CdrEndianness.LittleEndian, cdrOrigin: CdrEncapsulation.Size);
        AddTwoIntsResponseSerializer.Instance.Serialize(ref w, in response);
        return buffer.AsMemory(0, w.Position).ToArray();
    }

    // --- テスト本体 ---

    [Fact]
    public async Task CallAsync_は_related_sample_identity_で相関した_reply_を返す()
    {
        using var participant = CreateSingleParticipant(out _);

        var descriptor = new ServiceDescriptor<AddTwoIntsRequest, AddTwoIntsResponse>(
            "example_interfaces::srv::dds_::AddTwoInts_Request_",
            "example_interfaces::srv::dds_::AddTwoInts_Response_",
            AddTwoIntsRequestSerializer.Instance,
            AddTwoIntsResponseSerializer.Instance);

        using var client = participant.CreateServiceClient(descriptor, "add_two_ints");

        participant.Start();

        // リクエストを送信
        var callTask = client.CallAsync(new AddTwoIntsRequest(2, 3), TimeSpan.FromSeconds(5));

        // TCS が _pending に登録されるまで待つ (最大 2 秒)
        var spinDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < spinDeadline && client.PendingRequestCount < 1)
        {
            await Task.Delay(10);
        }

        client.PendingRequestCount.Should().Be(1, "リクエストが pending に登録されているはず");

        // SN=1 で相関キーを組み立てる (フレッシュな writer は SN=1 から開始)
        var sn = new SequenceNumber(1);
        var related = new SampleIdentity(client.RequestWriterGuid, sn);

        // inline QoS と reply ペイロードを構築して注入
        var inlineQos = RelatedSampleIdentityInlineQos.Build(related, CdrEndianness.LittleEndian);
        var change = new CacheChange(
            ChangeKind.Alive,
            client.RequestWriterGuid,
            sn,
            Time.Now(),
            BuildReplyPayload(new AddTwoIntsResponse(5)),
            inlineQos,
            CdrEndianness.LittleEndian);

        client.InjectReplyForTest(change);

        // CallAsync が正しい値を返すことを検証
        var result = await callTask.WaitAsync(TimeSpan.FromSeconds(5));
        result.Sum.Should().Be(5);
    }
}
