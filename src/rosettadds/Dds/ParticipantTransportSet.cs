using System.Net;
using System.Net.Sockets;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Dds;

/// <summary>
/// Domain Participant が使用する transport と、その所有権・ライフサイクルを管理する。
/// </summary>
internal sealed class ParticipantTransportSet : IDisposable
{
    private readonly OwnedTransport _metatrafficMulticast;
    private readonly OwnedTransport _metatrafficUnicast;
    private readonly OwnedTransport _userMulticast;
    private readonly OwnedTransport _userUnicast;
    private bool _started;
    private bool _disposed;

    private ParticipantTransportSet(
        OwnedTransport metatrafficMulticast,
        OwnedTransport metatrafficUnicast,
        OwnedTransport userMulticast,
        OwnedTransport userUnicast,
        Locator metatrafficMulticastDestination,
        Locator userMulticastDestination,
        int resolvedParticipantId)
    {
        _metatrafficMulticast = metatrafficMulticast;
        _metatrafficUnicast = metatrafficUnicast;
        _userMulticast = userMulticast;
        _userUnicast = userUnicast;
        MetatrafficMulticastDestination = metatrafficMulticastDestination;
        UserMulticastDestination = userMulticastDestination;
        ResolvedParticipantId = resolvedParticipantId;
    }

    public IRtpsTransport MetatrafficMulticast => _metatrafficMulticast.Transport;
    public IRtpsTransport MetatrafficUnicast => _metatrafficUnicast.Transport;
    public IRtpsTransport UserMulticast => _userMulticast.Transport;
    public IRtpsTransport UserUnicast => _userUnicast.Transport;
    public Locator MetatrafficMulticastDestination { get; }
    public Locator UserMulticastDestination { get; }
    public Locator MetatrafficUnicastLocator => MetatrafficUnicast.LocalLocator;
    public Locator DefaultUnicastLocator => UserUnicast.LocalLocator;

    /// <summary>実際に使用された Participant ID。auto-probe により入力値と異なる場合がある。</summary>
    public int ResolvedParticipantId { get; }

    public static ParticipantTransportSet Create(DomainParticipantOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        var created = new List<OwnedTransport>(4);
        try
        {
            int discoveryMulticastPort = RtpsPorts.DiscoveryMulticast(options.DomainId);
            int userMulticastPort = RtpsPorts.UserMulticast(options.DomainId);
            var localAddress = options.LocalUnicastAddress ?? IPAddress.Loopback;

            var metatrafficMulticast = Add(
                created,
                options.CustomMulticastTransport,
                () => UdpTransport.CreateMulticast(
                    options.MulticastGroup,
                    discoveryMulticastPort,
                    options.MulticastInterface,
                    options.Logger));
            var userMulticast = Add(
                created,
                options.CustomUserMulticastTransport,
                () => UdpTransport.CreateMulticast(
                    options.MulticastGroup,
                    userMulticastPort,
                    options.MulticastInterface,
                    options.Logger));

            bool hasCustomUnicast = options.CustomUnicastTransport is not null
                                    && options.CustomUserUnicastTransport is not null;
            int resolvedId = options.ParticipantId;

            OwnedTransport metatrafficUnicast;
            OwnedTransport userUnicast;

            if (hasCustomUnicast || !options.AutoProbeParticipantId)
            {
                int discoveryUnicastPort = RtpsPorts.DiscoveryUnicast(options.DomainId, resolvedId);
                int userUnicastPort = RtpsPorts.UserUnicast(options.DomainId, resolvedId);
                metatrafficUnicast = Add(
                    created,
                    options.CustomUnicastTransport,
                    () => UdpTransport.CreateUnicast(localAddress, discoveryUnicastPort, options.Logger));
                userUnicast = Add(
                    created,
                    options.CustomUserUnicastTransport,
                    () => UdpTransport.CreateUnicast(localAddress, userUnicastPort, options.Logger));
            }
            else
            {
                (metatrafficUnicast, userUnicast, resolvedId) =
                    ProbeUnicastTransports(created, options, localAddress);
            }

            if (resolvedId != options.ParticipantId)
            {
                options.Logger.Info(
                    $"ParticipantTransportSet: auto-probed ParticipantId {options.ParticipantId} -> {resolvedId}");
            }

            return new ParticipantTransportSet(
                metatrafficMulticast,
                metatrafficUnicast,
                userMulticast,
                userUnicast,
                Locator.FromUdpV4(options.MulticastGroup, (uint)discoveryMulticastPort),
                Locator.FromUdpV4(options.MulticastGroup, (uint)userMulticastPort),
                resolvedId);
        }
        catch
        {
            DisposeOwned(created);
            throw;
        }
    }

    private static (OwnedTransport metatraffic, OwnedTransport user, int participantId)
        ProbeUnicastTransports(
            List<OwnedTransport> created,
            DomainParticipantOptions options,
            IPAddress localAddress)
    {
        for (int id = options.ParticipantId; id <= RtpsConstants.MaxParticipantId; id++)
        {
            int discoveryPort = RtpsPorts.DiscoveryUnicast(options.DomainId, id);
            int userPort = RtpsPorts.UserUnicast(options.DomainId, id);
            var probed = new List<OwnedTransport>(2);
            try
            {
                var mt = Add(probed, options.CustomUnicastTransport,
                    () => UdpTransport.CreateUnicast(localAddress, discoveryPort, options.Logger));
                var ut = Add(probed, options.CustomUserUnicastTransport,
                    () => UdpTransport.CreateUnicast(localAddress, userPort, options.Logger));
                created.AddRange(probed);
                return (mt, ut, id);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                DisposeOwned(probed);
            }
        }

        throw new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_started)
        {
            return;
        }

        MetatrafficMulticast.Start();
        MetatrafficUnicast.Start();
        UserMulticast.Start();
        UserUnicast.Start();
        _started = true;
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        UserUnicast.Stop();
        UserMulticast.Stop();
        MetatrafficMulticast.Stop();
        MetatrafficUnicast.Stop();
        _started = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _userUnicast.DisposeIfOwned();
        _userMulticast.DisposeIfOwned();
        _metatrafficMulticast.DisposeIfOwned();
        _metatrafficUnicast.DisposeIfOwned();
    }

    private static OwnedTransport Add(
        ICollection<OwnedTransport> created,
        IRtpsTransport? customTransport,
        Func<IRtpsTransport> createTransport)
    {
        var ownedTransport = customTransport is null
            ? new OwnedTransport(createTransport(), ownsTransport: true)
            : new OwnedTransport(customTransport, ownsTransport: false);
        created.Add(ownedTransport);
        return ownedTransport;
    }

    private static void DisposeOwned(IEnumerable<OwnedTransport> transports)
    {
        foreach (var transport in transports.Reverse())
        {
            transport.DisposeIfOwned();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ParticipantTransportSet));
    }

    private sealed class OwnedTransport
    {
        private readonly bool _ownsTransport;

        public OwnedTransport(IRtpsTransport transport, bool ownsTransport)
        {
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _ownsTransport = ownsTransport;
        }

        public IRtpsTransport Transport { get; }

        public void DisposeIfOwned()
        {
            if (_ownsTransport)
            {
                Transport.Dispose();
            }
        }
    }
}
