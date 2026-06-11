namespace ROSettaDDS.Rtps.Submessages;

/// <summary>
/// 各 Submessage 共通およびキンド固有のフラグビット定義。RTPS 仕様 9.4.5.x。
/// flags フィールドの bit 0 は常にエンディアン (E) で 0=Big / 1=Little。
/// それ以外のビットは Submessage ごとに意味が異なる。
/// </summary>
public static class SubmessageFlags
{
    /// <summary>Endianness (E)。すべての Submessage 共通。1=LittleEndian。</summary>
    public const byte Endianness = 0x01;

    // -- DATA / DATA_FRAG --
    /// <summary>InlineQos 存在 (Q)。DATA / DATA_FRAG。</summary>
    public const byte DataInlineQos = 0x02;
    /// <summary>SerializedPayload にデータが入っている (D)。DATA。</summary>
    public const byte DataData = 0x04;
    /// <summary>SerializedPayload にキーが入っている (K)。DATA。</summary>
    public const byte DataKey = 0x08;
    /// <summary>Non-standard payload (N)。DATA。</summary>
    public const byte DataNonStandardPayload = 0x10;

    // -- HEARTBEAT --
    /// <summary>Final (F)。HEARTBEAT。1=応答不要。</summary>
    public const byte HeartbeatFinal = 0x02;
    /// <summary>Liveliness (L)。HEARTBEAT。1=Liveliness 維持目的。</summary>
    public const byte HeartbeatLiveliness = 0x04;

    // -- ACKNACK --
    /// <summary>Final (F)。ACKNACK。1=応答不要。</summary>
    public const byte AckNackFinal = 0x02;

    // -- INFO_TIMESTAMP --
    /// <summary>Invalidate (I)。INFO_TS。1=以降のタイムスタンプ無効化。</summary>
    public const byte InfoTsInvalidate = 0x02;

    // -- GAP --
    // (Endianness のみ)
}
