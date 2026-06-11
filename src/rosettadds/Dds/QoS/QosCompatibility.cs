using ROSettaDDS.Discovery;

namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// DDS QoS Request-vs-Offered (RxO) 互換性チェック。
/// DDS 仕様 2.2.3 "REQUESTED vs OFFERED" に基づく。
/// </summary>
public static class QosCompatibility
{
    /// <summary>
    /// writer (offered) と reader (requested) の QoS が互換かどうかを判定する。
    /// Reliability と Durability の両方を検査する。
    /// </summary>
    /// <param name="offeredWriter">writer 側の endpoint data (offered QoS)。</param>
    /// <param name="requestedReader">reader 側の endpoint data (requested QoS)。</param>
    /// <returns>互換であれば true、非互換であれば false。</returns>
    public static bool IsCompatible(DiscoveredEndpointData offeredWriter, DiscoveredEndpointData requestedReader)
    {
        // Reliability RxO: offered.Kind >= requested.Kind
        // Reliable(2) >= BestEffort(1): OK
        // BestEffort(1) >= Reliable(2): NG
        if ((int)offeredWriter.Reliability.Kind < (int)requestedReader.Reliability.Kind)
        {
            return false;
        }

        // Durability RxO: offered.Kind >= requested.Kind
        // Volatile=0 < TransientLocal=1 < Transient=2 < Persistent=3
        if ((int)offeredWriter.Durability.Kind < (int)requestedReader.Durability.Kind)
        {
            return false;
        }

        return true;
    }
}
