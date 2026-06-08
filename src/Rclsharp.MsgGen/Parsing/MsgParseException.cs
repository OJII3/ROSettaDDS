using System;

namespace Rclsharp.MsgGen.Parsing;

/// <summary>.msg の解析エラー。行番号と元ファイルを含む。</summary>
public sealed class MsgParseException : Exception
{
    public MsgParseException(string message)
        : base(message)
    {
    }
}
