namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// Presentation QoS の access_scope。DDS 仕様 7.1.3.6。
/// </summary>
public enum PresentationAccessScope
{
    Instance = 0,
    Topic = 1,
    Group = 2,
}
