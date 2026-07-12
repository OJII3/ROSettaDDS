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
        add
        {
            try
            {
                NetworkChange.NetworkAddressChanged += value;
            }
            catch (NotSupportedException)
            {
                // Unity IL2CPP/Android では NetworkChange の内部実装
                // (CreateNLSocket) が未対応のため、購読に失敗する。
                // 自動ネットワーク復旧は利用不可になるが DDS 通信は継続する。
            }
        }
        remove
        {
            try
            {
                NetworkChange.NetworkAddressChanged -= value;
            }
            catch (NotSupportedException)
            {
            }
        }
    }
}
