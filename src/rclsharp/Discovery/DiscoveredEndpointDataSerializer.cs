using Rclsharp.Cdr;
using Rclsharp.Cdr.ParameterList;
using Rclsharp.Common;
using Rclsharp.Dds.QoS;

using Guid = Rclsharp.Common.Guid;

namespace Rclsharp.Discovery;

/// <summary>
/// <see cref="DiscoveredEndpointData"/> を PL_CDR ParameterList に変換する。
/// 出力には CDR エンキャプスレーションヘッダ (PL_CDR_LE/BE) は含まない (Data submessage 構築側の責務)。
/// </summary>
public static class DiscoveredEndpointDataSerializer
{
    /// <summary>data を PL_CDR ParameterList として書き込む (SENTINEL 含む)。</summary>
    public static void Write(ref CdrWriter writer, DiscoveredEndpointData data)
    {
        var pl = new ParameterListWriter(writer);
        bool littleEndian = writer.Endianness == CdrEndianness.LittleEndian;

        // PARTICIPANT_GUID (16B)
        pl.BeginParameter(ParameterId.ParticipantGuid);
        var pgBytes = new byte[Guid.Size];
        data.ParticipantGuid.WriteTo(pgBytes);
        pl.WriteRawBytes(pgBytes);
        pl.EndParameter();

        // ENDPOINT_GUID (16B)
        pl.BeginParameter(ParameterId.EndpointGuid);
        var egBytes = new byte[Guid.Size];
        data.EndpointGuid.WriteTo(egBytes);
        pl.WriteRawBytes(egBytes);
        pl.EndParameter();

        // KEY_HASH (16B = endpoint guid)
        pl.BeginParameter(ParameterId.KeyHash);
        pl.WriteRawBytes(egBytes);
        pl.EndParameter();

        // TOPIC_NAME (string)
        pl.BeginParameter(ParameterId.TopicName);
        pl.WriteString(data.TopicName);
        pl.EndParameter();

        // TYPE_NAME (string)
        pl.BeginParameter(ParameterId.TypeName);
        pl.WriteString(data.TypeName);
        pl.EndParameter();

        // RELIABILITY (4B kind + 8B Duration = 12B)
        pl.BeginParameter(ParameterId.Reliability);
        pl.WriteInt32((int)data.Reliability.Kind);
        var blockingBytes = new byte[Duration.Size];
        data.Reliability.MaxBlockingTime.WriteTo(blockingBytes, littleEndian);
        pl.WriteRawBytes(blockingBytes);
        pl.EndParameter();

        // DURABILITY (4B kind)
        pl.BeginParameter(ParameterId.Durability);
        pl.WriteInt32((int)data.Durability.Kind);
        pl.EndParameter();

        // DEADLINE (8B Duration)
        pl.BeginParameter(ParameterId.Deadline);
        var deadlineBytes = new byte[Duration.Size];
        data.Deadline.Period.WriteTo(deadlineBytes, littleEndian);
        pl.WriteRawBytes(deadlineBytes);
        pl.EndParameter();

        // LATENCY_BUDGET (8B Duration)
        pl.BeginParameter(ParameterId.LatencyBudget);
        var latencyBytes = new byte[Duration.Size];
        data.LatencyBudget.Duration.WriteTo(latencyBytes, littleEndian);
        pl.WriteRawBytes(latencyBytes);
        pl.EndParameter();

        // LIVELINESS (4B kind + 8B Duration = 12B)
        pl.BeginParameter(ParameterId.Liveliness);
        pl.WriteInt32((int)data.Liveliness.Kind);
        var livelinessBytes = new byte[Duration.Size];
        data.Liveliness.LeaseDuration.WriteTo(livelinessBytes, littleEndian);
        pl.WriteRawBytes(livelinessBytes);
        pl.EndParameter();

        // OWNERSHIP (4B kind)
        pl.BeginParameter(ParameterId.Ownership);
        pl.WriteInt32((int)data.Ownership.Kind);
        pl.EndParameter();

        // DESTINATION_ORDER (4B kind)
        pl.BeginParameter(ParameterId.DestinationOrder);
        pl.WriteInt32((int)data.DestinationOrder.Kind);
        pl.EndParameter();

        // PRESENTATION (4B access_scope + 1B coherent + 1B ordered + 2B pad = 8B)
        pl.BeginParameter(ParameterId.Presentation);
        pl.WriteInt32((int)data.Presentation.AccessScope);
        pl.WriteBool(data.Presentation.CoherentAccess);
        pl.WriteBool(data.Presentation.OrderedAccess);
        pl.EndParameter();

        // PARTITION (sequence<string>)
        pl.BeginParameter(ParameterId.Partition);
        var partitionNames = data.Partition.Names;
        pl.WriteUInt32((uint)partitionNames.Count);
        foreach (var name in partitionNames)
            pl.WriteString(name);
        pl.EndParameter();

        // UNICAST_LOCATOR (24B each)
        foreach (var loc in data.UnicastLocators)
        {
            pl.BeginParameter(ParameterId.UnicastLocator);
            var locBytes = new byte[Locator.Size];
            loc.WriteTo(locBytes, littleEndian);
            pl.WriteRawBytes(locBytes);
            pl.EndParameter();
        }

        // MULTICAST_LOCATOR (24B each)
        foreach (var loc in data.MulticastLocators)
        {
            pl.BeginParameter(ParameterId.MulticastLocator);
            var locBytes = new byte[Locator.Size];
            loc.WriteTo(locBytes, littleEndian);
            pl.WriteRawBytes(locBytes);
            pl.EndParameter();
        }

        pl.WriteSentinel();
        writer = pl.CurrentWriter;
    }

    /// <summary>
    /// SEDP unregister/dispose 用の key payload を書き込む。
    /// Built-in endpoint instance の key は endpoint GUID として扱う。
    /// </summary>
    public static void WriteKey(ref CdrWriter writer, DiscoveredEndpointData data)
    {
        var pl = new ParameterListWriter(writer);
        var egBytes = new byte[Guid.Size];
        data.EndpointGuid.WriteTo(egBytes);

        pl.BeginParameter(ParameterId.EndpointGuid);
        pl.WriteRawBytes(egBytes);
        pl.EndParameter();

        pl.BeginParameter(ParameterId.KeyHash);
        pl.WriteRawBytes(egBytes);
        pl.EndParameter();

        pl.WriteSentinel();
        writer = pl.CurrentWriter;
    }

    /// <summary>PL_CDR ParameterList を読み出して <see cref="DiscoveredEndpointData"/> を生成する。</summary>
    public static DiscoveredEndpointData Read(ref CdrReader reader, EndpointKind kind)
    {
        var data = new DiscoveredEndpointData { Kind = kind };
        var pl = new ParameterListReader(reader);
        bool littleEndian = reader.Endianness == CdrEndianness.LittleEndian;

        while (pl.MoveNext(out var pid, out var length))
        {
            switch (ParameterId.StripFlags(pid))
            {
                case ParameterId.ParticipantGuid:
                    {
                        var raw = pl.CurrentValueRaw();
                        if (raw.Length >= Guid.Size)
                        {
                            data.ParticipantGuid = Guid.Read(raw[..Guid.Size]);
                        }
                        break;
                    }
                case ParameterId.EndpointGuid:
                    {
                        var raw = pl.CurrentValueRaw();
                        if (raw.Length >= Guid.Size)
                        {
                            data.EndpointGuid = Guid.Read(raw[..Guid.Size]);
                        }
                        break;
                    }
                case ParameterId.TopicName:
                    data.TopicName = pl.ReadString();
                    break;
                case ParameterId.TypeName:
                    data.TypeName = pl.ReadString();
                    break;
                case ParameterId.Reliability:
                    {
                        var rkind = (ReliabilityKind)pl.ReadInt32();
                        var raw = pl.CurrentValueRaw();
                        // 残り 8B が Duration
                        var blocking = raw.Length >= 12
                            ? Duration.Read(raw.Slice(4, 8), littleEndian)
                            : Duration.Zero;
                        data.Reliability = new ReliabilityQos(rkind, blocking);
                        break;
                    }
                case ParameterId.Durability:
                    data.Durability = new DurabilityQos((DurabilityKind)pl.ReadInt32());
                    break;
                case ParameterId.Deadline:
                    {
                        var raw = pl.CurrentValueRaw();
                        if (raw.Length >= Duration.Size)
                            data.Deadline = new DeadlineQos(Duration.Read(raw[..Duration.Size], littleEndian));
                        break;
                    }
                case ParameterId.LatencyBudget:
                    {
                        var raw = pl.CurrentValueRaw();
                        if (raw.Length >= Duration.Size)
                            data.LatencyBudget = new LatencyBudgetQos(Duration.Read(raw[..Duration.Size], littleEndian));
                        break;
                    }
                case ParameterId.Liveliness:
                    {
                        var lkind = (LivelinessKind)pl.ReadInt32();
                        var raw = pl.CurrentValueRaw();
                        var lease = raw.Length >= 12
                            ? Duration.Read(raw.Slice(4, 8), littleEndian)
                            : Duration.Infinite;
                        data.Liveliness = new LivelinessQos(lkind, lease);
                        break;
                    }
                case ParameterId.Ownership:
                    data.Ownership = new OwnershipQos((OwnershipKind)pl.ReadInt32());
                    break;
                case ParameterId.DestinationOrder:
                    data.DestinationOrder = new DestinationOrderQos((DestinationOrderKind)pl.ReadInt32());
                    break;
                case ParameterId.Presentation:
                    {
                        var scope = (PresentationAccessScope)pl.ReadInt32();
                        var coherent = pl.ReadBool();
                        var ordered = pl.ReadBool();
                        data.Presentation = new PresentationQos(scope, coherent, ordered);
                        break;
                    }
                case ParameterId.Partition:
                    {
                        var count = pl.ReadUInt32();
                        if (count > 256) break; // 不正パケットによる OOM 防止
                        var names = new string[count];
                        for (uint i = 0; i < count; i++)
                            names[i] = pl.ReadString();
                        data.Partition = new PartitionQos(names);
                        break;
                    }
                case ParameterId.KeyHash:
                    {
                        var raw = pl.CurrentValueRaw();
                        if (data.EndpointGuid.Equals(default) && raw.Length >= Guid.Size)
                        {
                            data.EndpointGuid = Guid.Read(raw[..Guid.Size]);
                            data.ParticipantGuid = new Guid(data.EndpointGuid.Prefix, data.ParticipantGuid.EntityId);
                        }
                        break;
                    }
                case ParameterId.UnicastLocator:
                    {
                        var raw = pl.CurrentValueRaw();
                        if (raw.Length >= Locator.Size)
                        {
                            data.UnicastLocators.Add(Locator.Read(raw[..Locator.Size], littleEndian));
                        }
                        break;
                    }
                case ParameterId.MulticastLocator:
                    {
                        var raw = pl.CurrentValueRaw();
                        if (raw.Length >= Locator.Size)
                        {
                            data.MulticastLocators.Add(Locator.Read(raw[..Locator.Size], littleEndian));
                        }
                        break;
                    }
                default:
                    // 未知 PID は MoveNext が次へ進める際に自動スキップ
                    break;
            }
        }

        reader = pl.CurrentReader;
        return data;
    }
}
