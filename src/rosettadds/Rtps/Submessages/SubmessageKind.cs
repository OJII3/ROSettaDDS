namespace ROSettaDDS.Rtps.Submessages;

/// <summary>
/// RTPS Submessage 種別 (1 バイト)。RTPS 仕様 9.4.5.1.1 / Table 9.14。
/// </summary>
public enum SubmessageKind : byte
{
    Pad = 0x01,
    AckNack = 0x06,
    Heartbeat = 0x07,
    Gap = 0x08,
    InfoTimestamp = 0x09,
    InfoSource = 0x0c,
    InfoReplyIp4 = 0x0d,
    InfoDestination = 0x0e,
    InfoReply = 0x0f,
    NackFrag = 0x12,
    HeartbeatFrag = 0x13,
    Data = 0x15,
    DataFrag = 0x16,
}
