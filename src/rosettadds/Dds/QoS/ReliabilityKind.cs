namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// DDS Reliability QoS の種別。RTPS 仕様 8.7.4 / Table 9.10。
/// 値はワイヤ形式 (PID_RELIABILITY) と一致。
/// </summary>
public enum ReliabilityKind
{
    BestEffort = 1,
    Reliable = 2,
}
