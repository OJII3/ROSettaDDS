using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ROSettaDDS.Transport;

/// <summary>
/// ローカル NIC の IPv4 アドレスを列挙するユーティリティ。
///
/// Fast DDS / Cyclone DDS の rmw 層が全 NIC を自動列挙して unicast locator を広告するのと同様に、
/// <see cref="Dds.DomainParticipantOptions.LocalUnicastAddress"/> /
/// <see cref="Rcl.ContextOptions.LocalUnicastAddress"/> 未指定時に
/// 全インターフェースの locator を SPDP/SEDP へ広告するために使う。
/// </summary>
public static class LocalNetwork
{
    /// <summary>
    /// 稼働中 (<see cref="OperationalStatus.Up"/>) の NIC が持つ IPv4 unicast アドレスを列挙する。
    /// loopback (127.0.0.1) を必ず含み、APIPA リンクローカル (169.254.0.0/16) は除外する。重複は排除する。
    /// </summary>
    public static IReadOnlyList<IPAddress> EnumerateUnicastIPv4()
    {
        var result = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();

        void Add(IPAddress addr)
        {
            if (seen.Add(addr))
            {
                result.Add(addr);
            }
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var addr = unicast.Address;
                if (addr.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }
                if (IsLinkLocal(addr))
                {
                    continue;
                }
                Add(addr);
            }
        }

        // 同一ホスト内通信のため loopback は常に広告する。
        Add(IPAddress.Loopback);
        return result;
    }

    /// <summary>APIPA リンクローカル (169.254.0.0/16) か判定する。</summary>
    private static bool IsLinkLocal(IPAddress addr)
    {
        var bytes = addr.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
