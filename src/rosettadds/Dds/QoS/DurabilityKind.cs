namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// DDS Durability QoS の種別。RTPS 仕様 8.7.2 / Table 9.10。
/// 値はワイヤ形式 (PID_DURABILITY) と一致。
/// </summary>
public enum DurabilityKind
{
    Volatile = 0,
    TransientLocal = 1,
    Transient = 2,
    Persistent = 3,
}
