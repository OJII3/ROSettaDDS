using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps;
using ROSettaDDS.Transport;
using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rcl;

/// <summary>
/// ROS 2 の rcl_context_t 相当。ドメイン共通の DDS 資源を所有する。
/// 1 プロセス内で複数 Node をホストできる。
/// </summary>
public sealed class Context : IDisposable
{
    private readonly ContextOptions _options;
    private bool _started;
    private bool _disposed;

    public Context(ContextOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _options = options;

        GuidPrefix = GuidPrefix.CreateForCurrentProcess(_options.VendorId);
        Guid = new Guid(GuidPrefix, BuiltinEntityIds.Participant);
    }

    public GuidPrefix GuidPrefix { get; }
    public Guid Guid { get; }
    public ContextOptions Options => _options;
    public ILogger Logger => _options.Logger;

    public int ResolvedParticipantId => throw new NotImplementedException();
    public IRtpsTransport UserMulticastTransport => throw new NotImplementedException();
    public IRtpsTransport UserUnicastTransport => throw new NotImplementedException();
    public Locator UserMulticastDestination => throw new NotImplementedException();
    public DiscoveryDb DiscoveryDb => throw new NotImplementedException();

    public void Start() => throw new NotImplementedException();
    public void Stop() => throw new NotImplementedException();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
