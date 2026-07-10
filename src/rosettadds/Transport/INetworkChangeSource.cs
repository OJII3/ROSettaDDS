using System.Net.NetworkInformation;

namespace ROSettaDDS.Transport;

internal interface INetworkChangeSource
{
    event NetworkAddressChangedEventHandler? NetworkAddressChanged;
}
