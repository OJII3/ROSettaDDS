using System.Buffers.Binary;

namespace ROSettaDDS.Common;

/// <summary>
/// RTPS EntityId (4 バイト)。RTPS 仕様 8.2.4.2。
/// 上位 3 バイトが entityKey、下位 1 バイトが entityKind。
/// ワイヤ上はビッグエンディアンの uint32 として読み書きされる。
/// </summary>
public readonly struct EntityId : IEquatable<EntityId>
{
    public const int Size = 4;

    /// <summary>
    /// ビッグエンディアン解釈の uint32。
    /// 上位 3 バイト = entityKey、下位 1 バイト = entityKind。
    /// 例: BuiltinParticipantWriter = 0x000100c2
    /// </summary>
    public uint Value { get; }

    public EntityId(uint value)
    {
        Value = value;
    }

    public EntityId(uint key, EntityKind kind)
    {
        if (key > 0x00FF_FFFFu)
        {
            throw new ArgumentOutOfRangeException(nameof(key),
                "EntityId key must fit in 24 bits (0x000000–0xFFFFFF).");
        }
        Value = (key << 8) | (uint)(byte)kind;
    }

    /// <summary>上位 24 ビットの entityKey。</summary>
    public uint Key => Value >> 8;

    /// <summary>下位 8 ビットの entityKind。</summary>
    public EntityKind Kind => (EntityKind)(byte)Value;

    /// <summary>ENTITYID_UNKNOWN (0x00000000)。</summary>
    public static readonly EntityId Unknown = new(0u);

    /// <summary>ENTITYID_PARTICIPANT (0x000001c1)。</summary>
    public static readonly EntityId Participant = new(0x0000_01c1u);

    /// <summary>Built-in (Discovery 用) エンドポイントかどうか。kind の上位 2 ビットが 11。</summary>
    public bool IsBuiltin => ((byte)Kind & 0xC0) == 0xC0;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException(
                $"Destination requires at least {Size} bytes.", nameof(destination));
        }
        BinaryPrimitives.WriteUInt32BigEndian(destination, Value);
    }

    public static EntityId Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException(
                $"Source requires at least {Size} bytes.", nameof(source));
        }
        return new EntityId(BinaryPrimitives.ReadUInt32BigEndian(source));
    }

    public bool Equals(EntityId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is EntityId e && Equals(e);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => $"0x{Value:X8}";

    public static bool operator ==(EntityId left, EntityId right) => left.Equals(right);
    public static bool operator !=(EntityId left, EntityId right) => !left.Equals(right);
}

/// <summary>
/// RTPS EntityKind (8 ビット)。RTPS 仕様 9.3.1.2 / Table 9.1。
/// 上位 2 ビットが Vendor specificity と Built-in 判別、下位 6 ビットがロール。
/// </summary>
public enum EntityKind : byte
{
    Unknown = 0x00,

    // User-defined endpoints
    UserDefinedParticipant = 0x01,
    UserDefinedWriterWithKey = 0x02,
    UserDefinedWriterNoKey = 0x03,
    UserDefinedReaderNoKey = 0x04,
    UserDefinedReaderWithKey = 0x07,
    UserDefinedWriterGroup = 0x08,
    UserDefinedReaderGroup = 0x09,

    // Built-in (Discovery) endpoints
    BuiltinParticipant = 0xc1,
    BuiltinWriterWithKey = 0xc2,
    BuiltinWriterNoKey = 0xc3,
    BuiltinReaderNoKey = 0xc4,
    BuiltinReaderWithKey = 0xc7,
    BuiltinWriterGroup = 0xc8,
    BuiltinReaderGroup = 0xc9,

    // Vendor-specific endpoints
    VendorWriterWithKey = 0x42,
    VendorReaderWithKey = 0x47,
}
