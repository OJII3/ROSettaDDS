using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ROSettaDDS.Cdr.ParameterList;

/// <summary>
/// PL_CDR ParameterList リーダ。RTPS 仕様 9.4.2.11。
/// <see cref="MoveNext"/> でパラメータを順次走査する。値を読まなかった場合、
/// 次の <see cref="MoveNext"/> で残りバイトが自動スキップされる。
/// </summary>
public ref struct ParameterListReader
{
    private CdrReader _reader;
    private ushort _currentLength;
    private int _currentValueStart;

    public CdrEndianness Endianness => _reader.Endianness;

    /// <summary>現在の内部 CdrReader のスナップショット。呼び出し元 reader に反映するには代入で受け戻す。</summary>
    public CdrReader CurrentReader => _reader;

    // 値読み出しのパススルー。ref 返しを避けるため、writer 側と同じく明示メソッドにする。
    public byte ReadByte() => _reader.ReadByte();
    public bool ReadBool() => _reader.ReadBool();
    public short ReadInt16() => _reader.ReadInt16();
    public ushort ReadUInt16() => _reader.ReadUInt16();
    public int ReadInt32() => _reader.ReadInt32();
    public uint ReadUInt32() => _reader.ReadUInt32();
    public long ReadInt64() => _reader.ReadInt64();
    public ulong ReadUInt64() => _reader.ReadUInt64();
    public float ReadFloat() => _reader.ReadFloat();
    public double ReadDouble() => _reader.ReadDouble();
    public string ReadString() => _reader.ReadString();
    public string ReadString(int maxBytes) => _reader.ReadString(maxBytes);
    public void AlignTo(int alignment) => _reader.AlignTo(alignment);
    public void Skip(int count) => _reader.Skip(count);

    public ParameterListReader(CdrReader reader)
    {
        _reader = reader;
        _currentLength = 0;
        _currentValueStart = -1;
    }

    /// <summary>
    /// 次のパラメータ先頭に進む。SENTINEL に達した場合は false を返す。
    /// 直前の値を <see cref="Inner"/> から完全に読み出していなくても、ここで自動スキップされる。
    /// </summary>
    public bool MoveNext(out ushort pid, out ushort length)
    {
        // 直前のパラメータの未読部分をスキップ
        if (_currentValueStart >= 0)
        {
            int consumed = _reader.Position - _currentValueStart;
            int remaining = _currentLength - consumed;
            if (remaining > 0)
            {
                _reader.Skip(remaining);
            }
            _currentValueStart = -1;
        }
        _reader.AlignTo(4);
        pid = _reader.ReadUInt16();
        length = _reader.ReadUInt16();
        if (pid == ParameterId.Sentinel)
        {
            return false;
        }
        _currentLength = length;
        _currentValueStart = _reader.Position;
        return true;
    }

    /// <summary>現在のパラメータの値部分の長さ (バイト)。</summary>
    public int CurrentValueLength => _currentLength;

    /// <summary>現在のパラメータ値領域 (生バイト) を取得する。<see cref="Inner"/> を直接読まずに済むショートカット。</summary>
    [UnscopedRef]
    public ReadOnlySpan<byte> CurrentValueRaw()
    {
        if (_currentValueStart < 0)
        {
            throw new InvalidOperationException("No parameter is being read.");
        }
        return _reader.RawBuffer.Slice(_currentValueStart, _currentLength);
    }
}
