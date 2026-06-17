using System;
using System.Linq;
using ROSettaDDS.MsgGen.Model;

namespace ROSettaDDS.MsgGen.Parsing;

/// <summary>
/// ROS 2 の <c>.srv</c> を request / response の 2 つの <see cref="MessageDefinition"/> に変換する。
/// <c>---</c> 単独行で request 部と response 部を分割し、各部を <see cref="MsgParser"/> で解析する。
/// </summary>
public static class SrvParser
{
    public static (MessageDefinition Request, MessageDefinition Response) Parse(
        string package, string serviceName, string text)
    {
        if (string.IsNullOrEmpty(package)) throw new ArgumentException("package is required", nameof(package));
        if (string.IsNullOrEmpty(serviceName)) throw new ArgumentException("serviceName is required", nameof(serviceName));

        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalized.Split('\n');

        int separator = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                separator = i;
                break;
            }
        }
        if (separator < 0)
        {
            throw new MsgParseException($"{serviceName}.srv: request/response 区切り '---' が見つかりません");
        }

        string requestText = string.Join("\n", lines.Take(separator));
        string responseText = string.Join("\n", lines.Skip(separator + 1));

        var request = MsgParser.Parse(package, serviceName + "_Request", requestText);
        var response = MsgParser.Parse(package, serviceName + "_Response", responseText);

        return (
            WithSrvNamespace(request),
            WithSrvNamespace(response));
    }

    private static MessageDefinition WithSrvNamespace(MessageDefinition def)
        => new(def.Package, def.Name, def.Constants, def.Fields, subNamespace: "srv");
}
