using Rclsharp.Cdr;
using Rclsharp.Cdr.ParameterList;
using Rclsharp.Common;

using Guid = Rclsharp.Common.Guid;

namespace Rclsharp.Discovery;

/// <summary>
/// <see cref="ParticipantData"/> を PL_CDR ParameterList としてシリアライズ/デシリアライズする。
/// 出力には CDR エンキャプスレーションヘッダ (PL_CDR_LE/BE) は含まない (Data submessage 構築側の責務)。
/// </summary>
public static class ParticipantDataSerializer
{
    /// <summary>
    /// data を PL_CDR ParameterList として書き込む (SENTINEL 含む)。
    /// </summary>
    public static void Write(ref CdrWriter writer, ParticipantData data)
    {
        var pl = new ParameterListWriter(writer);
        bool littleEndian = writer.Endianness == CdrEndianness.LittleEndian;

        // PROTOCOL_VERSION (2B + 2B pad)
        pl.BeginParameter(ParameterId.ProtocolVersion);
        pl.WriteByte(data.ProtocolVersion.Major);
        pl.WriteByte(data.ProtocolVersion.Minor);
        pl.EndParameter();

        // VENDORID (2B + 2B pad)
        pl.BeginParameter(ParameterId.VendorId);
        pl.WriteByte(data.VendorId.V0);
        pl.WriteByte(data.VendorId.V1);
        pl.EndParameter();

        // PARTICIPANT_GUID (16B)
        pl.BeginParameter(ParameterId.ParticipantGuid);
        var guidBytes = new byte[Guid.Size];
        data.Guid.WriteTo(guidBytes);
        pl.WriteRawBytes(guidBytes);
        pl.EndParameter();

        // BUILTIN_ENDPOINT_SET (4B)
        pl.BeginParameter(ParameterId.BuiltinEndpointSet);
        pl.WriteUInt32((uint)data.BuiltinEndpoints);
        pl.EndParameter();

        // PARTICIPANT_LEASE_DURATION (8B Duration_t)
        pl.BeginParameter(ParameterId.ParticipantLeaseDuration);
        var durationBytes = new byte[Duration.Size];
        data.LeaseDuration.WriteTo(durationBytes, littleEndian);
        pl.WriteRawBytes(durationBytes);
        pl.EndParameter();

        // EXPECTS_INLINE_QOS (1B bool + 3B pad)
        pl.BeginParameter(ParameterId.ExpectsInlineQos);
        pl.WriteBool(data.ExpectsInlineQos);
        pl.EndParameter();

        WriteLocators(ref pl, ParameterId.MetatrafficUnicastLocator, data.MetatrafficUnicastLocators, littleEndian);
        WriteLocators(ref pl, ParameterId.MetatrafficMulticastLocator, data.MetatrafficMulticastLocators, littleEndian);
        WriteLocators(ref pl, ParameterId.DefaultUnicastLocator, data.DefaultUnicastLocators, littleEndian);
        WriteLocators(ref pl, ParameterId.DefaultMulticastLocator, data.DefaultMulticastLocators, littleEndian);

        // ENTITY_NAME (任意)
        if (!string.IsNullOrEmpty(data.EntityName))
        {
            pl.BeginParameter(ParameterId.EntityName);
            pl.WriteString(data.EntityName);
            pl.EndParameter();
        }

        pl.WriteSentinel();

        // pl は内部に CdrWriter のコピーを保持しているため、
        // 呼び出し元の writer を進めるには明示的に受け戻す。
        writer = pl.CurrentWriter;
    }

    private static void WriteLocators(ref ParameterListWriter pl, ushort pid, List<Locator> locators, bool littleEndian)
    {
        foreach (var loc in locators)
        {
            pl.BeginParameter(pid);
            var bytes = new byte[Locator.Size];
            loc.WriteTo(bytes, littleEndian);
            pl.WriteRawBytes(bytes);
            pl.EndParameter();
        }
    }

    /// <summary>
    /// PL_CDR ParameterList を読み出して <see cref="ParticipantData"/> を生成する。
    /// 未知 PID はスキップする。ただし must-understand PID は拒否する。
    /// </summary>
    public static ParticipantData Read(ref CdrReader reader, DiscoveryLimits? limits = null)
    {
        limits ??= DiscoveryLimits.Default;
        var data = new ParticipantData();
        var pl = new ParameterListReader(reader);
        bool littleEndian = reader.Endianness == CdrEndianness.LittleEndian;

        while (pl.MoveNext(out var pid, out var length))
        {
            switch (ParameterId.StripFlags(pid))
            {
                case ParameterId.ProtocolVersion:
                    data.ProtocolVersion = new ProtocolVersion(pl.ReadByte(), pl.ReadByte());
                    break;

                case ParameterId.VendorId:
                    data.VendorId = new VendorId(pl.ReadByte(), pl.ReadByte());
                    break;

                case ParameterId.ParticipantGuid:
                    {
                        var raw = pl.CurrentValueRaw();
                        if (raw.Length >= Guid.Size)
                        {
                            data.Guid = Guid.Read(raw[..Guid.Size]);
                        }
                        break;
                    }

                case ParameterId.BuiltinEndpointSet:
                    data.BuiltinEndpoints = (BuiltinEndpointSet)pl.ReadUInt32();
                    break;

                case ParameterId.ParticipantLeaseDuration:
                    {
                        var raw = pl.CurrentValueRaw();
                        if (raw.Length >= Duration.Size)
                        {
                            data.LeaseDuration = Duration.Read(raw[..Duration.Size], littleEndian);
                        }
                        break;
                    }

                case ParameterId.ExpectsInlineQos:
                    data.ExpectsInlineQos = pl.ReadBool();
                    break;

                case ParameterId.MetatrafficUnicastLocator:
                    AppendLocator(pl.CurrentValueRaw(), littleEndian, data, data.MetatrafficUnicastLocators, limits);
                    break;
                case ParameterId.MetatrafficMulticastLocator:
                    AppendLocator(pl.CurrentValueRaw(), littleEndian, data, data.MetatrafficMulticastLocators, limits);
                    break;
                case ParameterId.DefaultUnicastLocator:
                    AppendLocator(pl.CurrentValueRaw(), littleEndian, data, data.DefaultUnicastLocators, limits);
                    break;
                case ParameterId.DefaultMulticastLocator:
                    AppendLocator(pl.CurrentValueRaw(), littleEndian, data, data.DefaultMulticastLocators, limits);
                    break;

                case ParameterId.EntityName:
                    data.EntityName = pl.ReadString(limits.MaxEntityNameBytes);
                    break;

                case ParameterId.DomainTagBase:
                    // PID_DOMAIN_TAG is a known DDSI-RTPS 2.5 must-understand PID.
                    // rclsharp does not use it for matching yet, but it must not make SPDP fail.
                    break;

                default:
                    ParameterId.ThrowIfUnknownMustUnderstand(pid);
                    break;
            }
        }

        reader = pl.CurrentReader;
        data.LeaseDuration = limits.ClampRemoteParticipantLeaseDuration(data.LeaseDuration);
        return data;
    }

    private static void AppendLocator(
        ReadOnlySpan<byte> raw,
        bool littleEndian,
        ParticipantData data,
        List<Locator> dest,
        DiscoveryLimits limits)
    {
        if (raw.Length >= Locator.Size)
        {
            if (TotalLocatorCount(data) >= limits.MaxParticipantLocators)
            {
                throw new InvalidDataException(
                    $"Participant locator count exceeds limit {limits.MaxParticipantLocators}.");
            }
            dest.Add(Locator.Read(raw[..Locator.Size], littleEndian));
        }
    }

    private static int TotalLocatorCount(ParticipantData data)
        => data.MetatrafficUnicastLocators.Count
         + data.MetatrafficMulticastLocators.Count
         + data.DefaultUnicastLocators.Count
         + data.DefaultMulticastLocators.Count;
}
