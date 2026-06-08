using System.Buffers.Binary;

namespace Rclsharp.Cdr.ParameterList;

/// <summary>
/// PL_CDR ParameterList ライタ。RTPS 仕様 9.4.2.11。
/// 各パラメータは PID(uint16) + length(uint16) + value (4B 整列) で構成される。
/// 末尾には <see cref="ParameterId.Sentinel"/> (length=0) を書く必要がある。
/// </summary>
/// <remarks>
/// パラメータ値の内部 alignment は外側の <see cref="CdrWriter"/> の <c>cdrOrigin</c> を継承する。
/// PL_CDR で使われる主要な値型 (uint8/16/32, Time_t/Duration_t (= 4B+4B), Locator, Guid, ProtocolVersion, VendorId)
/// は最大 4B 整列で十分なため、この単純化で問題ない。
/// </remarks>
public ref struct ParameterListWriter
{
    private CdrWriter _writer;
    private int _currentLengthPos;   // -1 if no open parameter
    private int _currentValueStart;

    public CdrEndianness Endianness => _writer.Endianness;

    /// <summary>現在の内部 CdrWriter のスナップショット。呼び出し元 writer に反映するには代入で受け戻す。</summary>
    public CdrWriter CurrentWriter => _writer;

    // 以下、値書き込みは開いているパラメータ値領域へ内部 CdrWriter を通して行うパススルー。
    // ref 返し (C# 11 scoped ref) が使えない処理系でも通るよう、ref プロパティではなく
    // 明示的メソッドを公開している。
    public void WriteByte(byte value) => _writer.WriteByte(value);
    public void WriteBool(bool value) => _writer.WriteBool(value);
    public void WriteInt16(short value) => _writer.WriteInt16(value);
    public void WriteUInt16(ushort value) => _writer.WriteUInt16(value);
    public void WriteInt32(int value) => _writer.WriteInt32(value);
    public void WriteUInt32(uint value) => _writer.WriteUInt32(value);
    public void WriteInt64(long value) => _writer.WriteInt64(value);
    public void WriteUInt64(ulong value) => _writer.WriteUInt64(value);
    public void WriteFloat(float value) => _writer.WriteFloat(value);
    public void WriteDouble(double value) => _writer.WriteDouble(value);
    public void WriteString(string? value) => _writer.WriteString(value);
    public void WriteString(ReadOnlySpan<char> value) => _writer.WriteString(value);
    public void WriteRawBytes(ReadOnlySpan<byte> bytes) => _writer.WriteRawBytes(bytes);
    public void AlignTo(int alignment) => _writer.AlignTo(alignment);

    public ParameterListWriter(CdrWriter writer)
    {
        _writer = writer;
        _currentLengthPos = -1;
        _currentValueStart = -1;
    }

    /// <summary>
    /// 新規パラメータを開始する。PID と length プレースホルダ (0) を書き、
    /// 呼び出し側は <see cref="Inner"/> 経由で値を書く。書き終えたら <see cref="EndParameter"/>。
    /// </summary>
    public void BeginParameter(ushort pid)
    {
        if (_currentLengthPos >= 0)
        {
            throw new InvalidOperationException("Previous parameter is not ended.");
        }
        _writer.AlignTo(4);
        _writer.WriteUInt16(pid);
        _currentLengthPos = _writer.Position;
        _writer.WriteUInt16(0); // length placeholder
        _currentValueStart = _writer.Position;
    }

    /// <summary>
    /// 現在のパラメータを閉じる。値を 4B 境界にパディングし、length フィールドへ書き戻す。
    /// </summary>
    public void EndParameter()
    {
        if (_currentLengthPos < 0)
        {
            throw new InvalidOperationException("No parameter is open.");
        }
        int valueLen = _writer.Position - _currentValueStart;
        int paddedLen = (valueLen + 3) & ~3;
        // Pad to 4 with zeros
        for (int i = valueLen; i < paddedLen; i++)
        {
            _writer.WriteByte(0);
        }
        if (paddedLen > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"Parameter value length {paddedLen} exceeds uint16 maximum.");
        }
        // Patch length field
        Span<byte> lenSlot = _writer.RawBuffer.Slice(_currentLengthPos, 2);
        if (_writer.Endianness == CdrEndianness.LittleEndian)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(lenSlot, (ushort)paddedLen);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(lenSlot, (ushort)paddedLen);
        }
        _currentLengthPos = -1;
        _currentValueStart = -1;
    }

    /// <summary>
    /// SENTINEL マーカーを書く。ParameterList の終端必須。
    /// </summary>
    public void WriteSentinel()
    {
        if (_currentLengthPos >= 0)
        {
            throw new InvalidOperationException("Previous parameter is not ended.");
        }
        _writer.AlignTo(4);
        _writer.WriteUInt16(ParameterId.Sentinel);
        _writer.WriteUInt16(0);
    }
}
