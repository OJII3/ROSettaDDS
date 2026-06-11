namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// Liveliness QoS の種別。DDS 仕様 7.1.3.11。
/// </summary>
public enum LivelinessKind
{
    Automatic = 0,
    ManualByParticipant = 1,
    ManualByTopic = 2,
}
