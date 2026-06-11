using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace ROSettaDDS.Cdr;

/// <summary>
/// OMG CDR (Common Data Representation) ライタ。<see cref="Span{T}"/> 上に直接書き込む。
/// alignment はストリーム先頭ではなく <c>cdrOrigin</c> からのオフセットで計算する
/// (カプセルヘッダ 4 バイトの後からデータを書く場合、cdrOrigin にカプセルヘッダ末尾位置を指定)。
/// 各プリミティブはサイズ境界に整列される。境界調整時に挿入されるパディングはゼロで埋める。
/// </summary>
public ref struct CdrWriter
{
    private readonly Span<byte> _buffer;
    private readonly int _cdrOrigin;
    private int _position;

    public CdrEndianness Endianness { get; }

    public CdrWriter(Span<byte> buffer, CdrEndianness endianness, int cdrOrigin = 0)
    {
        _buffer = buffer;
        _cdrOrigin = cdrOrigin;
        _position = cdrOrigin;
        Endianness = endianness;
    }

    /// <summary>現在位置 (バッファ先頭からのバイトオフセット)。</summary>
    public int Position => _position;

    /// <summary>書き込んだバイト数 (cdrOrigin より前は数えない)。</summary>
    public int BytesWritten => _position - _cdrOrigin;

    /// <summary>これまで書き込んだ範囲 (バッファ先頭から現在位置まで)。</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer[.._position];

    /// <summary>cdrOrigin から見たストリーム位置 (alignment の基準)。</summary>
    public int StreamOffset => _position - _cdrOrigin;

    /// <summary>書き込み先のバッファ全体 (デバッグ・上位連携用)。</summary>
    [UnscopedRef]
    public Span<byte> RawBuffer => _buffer;

    /// <summary>境界調整。挿入したパディングは 0 で埋める。alignment は 1/2/4/8 を想定。</summary>
    public void AlignTo(int alignment)
    {
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentException("Alignment must be a positive power of two.", nameof(alignment));
        }
        int offset = StreamOffset;
        int padding = (alignment - (offset & (alignment - 1))) & (alignment - 1);
        if (padding == 0)
        {
            return;
        }
        EnsureAvailable(padding);
        _buffer.Slice(_position, padding).Clear();
        _position += padding;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureAvailable(int byteCount)
    {
        if (_position + byteCount > _buffer.Length)
        {
            throw new InvalidOperationException(
                $"CdrWriter buffer overflow: needed {byteCount} bytes at position {_position} but capacity is {_buffer.Length}.");
        }
    }

    public void WriteByte(byte value)
    {
        EnsureAvailable(1);
        _buffer[_position++] = value;
    }

    public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteInt16(short value)
    {
        AlignTo(2);
        EnsureAvailable(2);
        if (Endianness == CdrEndianness.LittleEndian)
        {
            BinaryPrimitives.WriteInt16LittleEndian(_buffer.Slice(_position), value);
        }
        else
        {
            BinaryPrimitives.WriteInt16BigEndian(_buffer.Slice(_position), value);
        }
        _position += 2;
    }

    public void WriteUInt16(ushort value)
    {
        AlignTo(2);
        EnsureAvailable(2);
        if (Endianness == CdrEndianness.LittleEndian)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_position), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(_buffer.Slice(_position), value);
        }
        _position += 2;
    }

    public void WriteInt32(int value)
    {
        AlignTo(4);
        EnsureAvailable(4);
        if (Endianness == CdrEndianness.LittleEndian)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_position), value);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(_buffer.Slice(_position), value);
        }
        _position += 4;
    }

    public void WriteUInt32(uint value)
    {
        AlignTo(4);
        EnsureAvailable(4);
        if (Endianness == CdrEndianness.LittleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(_position), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(_buffer.Slice(_position), value);
        }
        _position += 4;
    }

    public void WriteInt64(long value)
    {
        AlignTo(8);
        EnsureAvailable(8);
        if (Endianness == CdrEndianness.LittleEndian)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_position), value);
        }
        else
        {
            BinaryPrimitives.WriteInt64BigEndian(_buffer.Slice(_position), value);
        }
        _position += 8;
    }

    public void WriteUInt64(ulong value)
    {
        AlignTo(8);
        EnsureAvailable(8);
        if (Endianness == CdrEndianness.LittleEndian)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.Slice(_position), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt64BigEndian(_buffer.Slice(_position), value);
        }
        _position += 8;
    }

    public void WriteFloat(float value)
    {
        AlignTo(4);
        EnsureAvailable(4);
        int bits = BitConverter.SingleToInt32Bits(value);
        if (Endianness == CdrEndianness.LittleEndian)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_position), bits);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(_buffer.Slice(_position), bits);
        }
        _position += 4;
    }

    public void WriteDouble(double value)
    {
        AlignTo(8);
        EnsureAvailable(8);
        long bits = BitConverter.DoubleToInt64Bits(value);
        if (Endianness == CdrEndianness.LittleEndian)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_position), bits);
        }
        else
        {
            BinaryPrimitives.WriteInt64BigEndian(_buffer.Slice(_position), bits);
        }
        _position += 8;
    }

    /// <summary>整列なし・長さプレフィックスなしで生バイト列を追記する。</summary>
    public void WriteRawBytes(ReadOnlySpan<byte> bytes)
    {
        EnsureAvailable(bytes.Length);
        bytes.CopyTo(_buffer.Slice(_position));
        _position += bytes.Length;
    }

    /// <summary>
    /// CDR string を書き込む。
    /// 形式: uint32 length(NUL を含む) + UTF-8 バイト列 + NUL バイト。
    /// 後続フィールドの整列はこの関数では行わない (次の Write* で必要に応じて調整される)。
    /// </summary>
    public void WriteString(ReadOnlySpan<char> value)
    {
        int byteCount = value.IsEmpty ? 0 : Encoding.UTF8.GetByteCount(value);
        uint length = (uint)(byteCount + 1); // include NUL terminator
        WriteUInt32(length);
        EnsureAvailable(byteCount + 1);
        if (byteCount > 0)
        {
            int written = Encoding.UTF8.GetBytes(value, _buffer.Slice(_position));
            _position += written;
        }
        _buffer[_position++] = 0; // NUL terminator
    }

    public void WriteString(string? value) => WriteString((value ?? string.Empty).AsSpan());

    /// <summary>
    /// シーケンス長 (uint32) を書き込む。要素本体はこの後に書き込み側が個別に書く。
    /// </summary>
    public void WriteSequenceLength(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Sequence length must be non-negative.");
        }
        WriteUInt32((uint)length);
    }
}
