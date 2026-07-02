using ROSettaDDS.Common.Logging;

namespace ROSettaDDS.Rcl;

public sealed class NodeOptions
{
    public ILogger? Logger { get; init; }

    public static NodeOptions Default { get; } = new();
}
