using System.Net.NetworkInformation;

namespace ROSettaDDS.Transport;

internal sealed class SystemNetworkChangeSource : INetworkChangeSource
{
    public static SystemNetworkChangeSource Instance { get; } = new();

    private SystemNetworkChangeSource()
    {
    }

    public event NetworkAddressChangedEventHandler? NetworkAddressChanged
    {
        add => NetworkChange.NetworkAddressChanged += value;
        remove => NetworkChange.NetworkAddressChanged -= value;
    }
}
