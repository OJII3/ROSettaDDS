namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// Ownership QoS の種別。DDS 仕様 7.1.3.17。
/// </summary>
public enum OwnershipKind
{
    Shared = 0,
    Exclusive = 1,
}
