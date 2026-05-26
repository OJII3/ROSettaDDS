using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace Rclsharp.Cdr;

/// <summary>
/// OMG CDR (Common Data Representation) リーダ。<see cref="ReadOnlySpan{T}"/> から直接読み出す。
/// alignment はストリーム先頭ではなく <c>cdrOrigin</c> からのオフセットで計算する。
/// </summary>
public ref struct CdrReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private readonly int _cdrOrigin;
    private readonly CdrReadLimits _limits;
    private int _position;

    public CdrEndianness Endianness { get; }

    public CdrReader(
        ReadOnlySpan<byte> buffer,
        CdrEndianness endianness,
        int cdrOrigin = 0,
        CdrReadLimits? limits = null)
    {
        if (cdrOrigin < 0 || cdrOrigin > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(cdrOrigin));
        }
        _buffer = buffer;
        _cdrOrigin = cdrOrigin;
        _limits = limits ?? CdrReadLimits.Default;
        _position = cdrOrigin;
        Endianness = endianness;
    }

    public int Position => _position;
    public int BytesRead => _position - _cdrOrigin;
    public int Remaining => _buffer.Length - _position;
    public int StreamOffset => _position - _cdrOrigin;

    [UnscopedRef]
    public ReadOnlySpan<byte> RawBuffer => _buffer;

    /// <summary>境界調整。スキップしたバイトの中身は検証しない。</summary>
    public void AlignTo(int alignment)
    {
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentException("Alignment must be a positive power of two.", nameof(alignment));
        }
        int offset = StreamOffset;
        int padding = (alignment - (offset & (alignment - 1))) & (alignment - 1);
        EnsureAvailable(padding);
        _position += padding;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureAvailable(int byteCount)
    {
        if (byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }
        if (byteCount > Remaining)
        {
            throw new InvalidOperationException(
                $"CdrReader buffer underflow: needed {byteCount} bytes at position {_position} but only {Remaining} bytes remain.");
        }
    }

    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _buffer[_position++];
    }

    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

    public bool ReadBool() => ReadByte() != 0;

    public short ReadInt16()
    {
        AlignTo(2);
        EnsureAvailable(2);
        short value = Endianness == CdrEndianness.LittleEndian
            ? BinaryPrimitives.ReadInt16LittleEndian(_buffer.Slice(_position))
            : BinaryPrimitives.ReadInt16BigEndian(_buffer.Slice(_position));
        _position += 2;
        return value;
    }

    public ushort ReadUInt16()
    {
        AlignTo(2);
        EnsureAvailable(2);
        ushort value = Endianness == CdrEndianness.LittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position))
            : BinaryPrimitives.ReadUInt16BigEndian(_buffer.Slice(_position));
        _position += 2;
        return value;
    }

    public int ReadInt32()
    {
        AlignTo(4);
        EnsureAvailable(4);
        int value = Endianness == CdrEndianness.LittleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position))
            : BinaryPrimitives.ReadInt32BigEndian(_buffer.Slice(_position));
        _position += 4;
        return value;
    }

    public uint ReadUInt32()
    {
        AlignTo(4);
        EnsureAvailable(4);
        uint value = Endianness == CdrEndianness.LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_position))
            : BinaryPrimitives.ReadUInt32BigEndian(_buffer.Slice(_position));
        _position += 4;
        return value;
    }

    public long ReadInt64()
    {
        AlignTo(8);
        EnsureAvailable(8);
        long value = Endianness == CdrEndianness.LittleEndian
            ? BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position))
            : BinaryPrimitives.ReadInt64BigEndian(_buffer.Slice(_position));
        _position += 8;
        return value;
    }

    public ulong ReadUInt64()
    {
        AlignTo(8);
        EnsureAvailable(8);
        ulong value = Endianness == CdrEndianness.LittleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_position))
            : BinaryPrimitives.ReadUInt64BigEndian(_buffer.Slice(_position));
        _position += 8;
        return value;
    }

    public float ReadFloat()
    {
        AlignTo(4);
        EnsureAvailable(4);
        int bits = Endianness == CdrEndianness.LittleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position))
            : BinaryPrimitives.ReadInt32BigEndian(_buffer.Slice(_position));
        _position += 4;
        return BitConverter.Int32BitsToSingle(bits);
    }

    public double ReadDouble()
    {
        AlignTo(8);
        EnsureAvailable(8);
        long bits = Endianness == CdrEndianness.LittleEndian
            ? BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position))
            : BinaryPrimitives.ReadInt64BigEndian(_buffer.Slice(_position));
        _position += 8;
        return BitConverter.Int64BitsToDouble(bits);
    }

    /// <summary>整列なし・指定バイト数を生で読み出す。返却 Span は内部バッファのスライス (寿命は呼び出し中)。</summary>
    public ReadOnlySpan<byte> ReadRawBytes(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        EnsureAvailable(count);
        var slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }

    /// <summary>指定バイト数を読み飛ばす (内容は検証しない)。</summary>
    public void Skip(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        EnsureAvailable(count);
        _position += count;
    }

    /// <summary>
    /// CDR string を読み出す。
    /// 形式: uint32 length(NUL を含む) + UTF-8 バイト列 + NUL バイト。
    /// 戻り値は NUL を除いた文字列。
    /// </summary>
    public string ReadString()
        => ReadString(_limits.MaxStringBytes);

    public string ReadString(int maxBytes)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        uint length = ReadUInt32();
        if (length == 0)
        {
            throw new InvalidDataException("CDR string length must include a NUL terminator.");
        }
        if (length > int.MaxValue)
        {
            throw new InvalidDataException($"CDR string length {length} exceeds Int32.MaxValue.");
        }
        if (length > maxBytes)
        {
            throw new InvalidDataException(
                $"CDR string length {length} exceeds limit {maxBytes}.");
        }
        EnsureAvailable((int)length);

        // length は NUL 終端を含む
        int payloadLength = (int)length - 1;
        if (_buffer[_position + payloadLength] != 0)
        {
            throw new InvalidDataException("CDR string is not NUL terminated.");
        }
        ReadOnlySpan<byte> payload = _buffer.Slice(_position, payloadLength);
        _position += (int)length; // NUL 含めて進める
        return payloadLength == 0 ? string.Empty : Encoding.UTF8.GetString(payload);
    }

    /// <summary>シーケンス長 (uint32) を読み出す。</summary>
    public int ReadSequenceLength()
    {
        uint length = ReadUInt32();
        if (length > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Sequence length {length} exceeds Int32.MaxValue.");
        }
        if (length > _limits.MaxSequenceElements)
        {
            throw new InvalidDataException(
                $"Sequence length {length} exceeds limit {_limits.MaxSequenceElements}.");
        }
        return (int)length;
    }

    /// <summary>
    /// 固定長要素 sequence の長さを読み、確保前に payload の上限と残量を検証する。
    /// </summary>
    public int ReadSequenceLength(int elementSize, int elementAlignment)
    {
        int count = ReadSequenceLength();
        EnsureSequencePayloadAvailable(count, elementSize, elementAlignment);
        return count;
    }

    public void EnsureSequencePayloadAvailable(int count, int elementSize, int elementAlignment)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        if (elementSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elementSize));
        }
        if (elementAlignment <= 0 || (elementAlignment & (elementAlignment - 1)) != 0)
        {
            throw new ArgumentException("Alignment must be a positive power of two.", nameof(elementAlignment));
        }
        if (count == 0)
        {
            return;
        }

        long payloadBytes = (long)count * elementSize;
        if (payloadBytes > _limits.MaxSequenceBytes)
        {
            throw new InvalidDataException(
                $"Sequence payload size {payloadBytes} bytes exceeds limit {_limits.MaxSequenceBytes}.");
        }

        int offset = StreamOffset;
        int padding = (elementAlignment - (offset & (elementAlignment - 1))) & (elementAlignment - 1);
        long requiredBytes = padding + payloadBytes;
        if (requiredBytes > Remaining)
        {
            throw new InvalidDataException(
                $"Sequence payload requires {requiredBytes} bytes at position {_position} but only {Remaining} bytes remain.");
        }
    }
}
