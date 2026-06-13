using System.Net;
using System.Net.Sockets;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Tests.Transport;

public class LocalNetworkTests
{
    [Fact]
    public void 列挙結果はloopbackを必ず含む()
    {
        var addresses = LocalNetwork.EnumerateUnicastIPv4();
        addresses.Should().Contain(IPAddress.Loopback);
    }

    [Fact]
    public void 列挙結果はすべてIPv4で重複しない()
    {
        var addresses = LocalNetwork.EnumerateUnicastIPv4();

        addresses.Should().OnlyContain(a => a.AddressFamily == AddressFamily.InterNetwork);
        addresses.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void 列挙結果にAPIPAリンクローカルを含めない()
    {
        var addresses = LocalNetwork.EnumerateUnicastIPv4();

        addresses.Select(a => a.GetAddressBytes())
            .Should().NotContain(b => b[0] == 169 && b[1] == 254);
    }
}
