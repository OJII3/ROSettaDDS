namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// DestinationOrder QoS の種別。DDS 仕様 7.1.3.13。
/// </summary>
public enum DestinationOrderKind
{
    ByReceptionTimestamp = 0,
    BySourceTimestamp = 1,
}
