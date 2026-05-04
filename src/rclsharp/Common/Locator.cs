using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace Rclsharp.Common;

/// <summary>
/// RTPS Locator_t (24 バイト)。RTPS 仕様 8.3.3 / 9.6.1。
/// kind (int32) + port (uint32) + address (16 バイト固定)。
/// IPv4 の場合は address の先頭 12 バイトをゼロパディングし、末尾 4 バイトに IPv4 アドレスを格納する。
/// </summary>
public struct Locator : IEquatable<Locator>
{
    public const int Size = 24;
    public const int AddressSize = 16;

    public LocatorKind Kind { get; }
    public uint Port { get; }
    private LocatorAddressStorage _address;

    public Locator(LocatorKind kind, uint port, ReadOnlySpan<byte> address)
    {
        if (address.Length != AddressSize)
        {
            throw new ArgumentException(
                $"Locator address requires exactly {AddressSize} bytes, got {address.Length}.",
                nameof(address));
        }
        Kind = kind;
        Port = port;
        _address = default;
        address.CopyTo(_address.AsSpan());
    }

    /// <summary>すべての値が無効な LOCATOR_INVALID。</summary>
    public static Locator Invalid
    {
        get
        {
            Span<byte> zero = stackalloc byte[AddressSize];
            return new Locator(LocatorKind.Invalid, 0u, zero);
        }
    }

    /// <summary>UDPv4 Locator を IP アドレスとポートから生成。先頭 12 バイトはゼロパディング。</summary>
    public static Locator FromUdpV4(IPAddress address, uint port)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentException("FromUdpV4 requires an IPv4 address.", nameof(address));
        }
        Span<byte> bytes = stackalloc byte[AddressSize];
        bytes.Clear();
        Span<byte> ipv4 = bytes[12..];
        if (!address.TryWriteBytes(ipv4, out int written) || written != 4)
        {
            throw new ArgumentException("Failed to encode IPv4 address.", nameof(address));
        }
        return new Locator(LocatorKind.UdpV4, port, bytes);
    }

    /// <summary>UDPv6 Locator を IP アドレスとポートから生成。</summary>
    public static Locator FromUdpV6(IPAddress address, uint port)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new ArgumentException("FromUdpV6 requires an IPv6 address.", nameof(address));
        }
        Span<byte> bytes = stackalloc byte[AddressSize];
        bytes.Clear();
        if (!address.TryWriteBytes(bytes, out int written) || written != AddressSize)
        {
            throw new ArgumentException("Failed to encode IPv6 address.", nameof(address));
        }
        return new Locator(LocatorKind.UdpV6, port, bytes);
    }

    public byte this[int index] => _address.ElementAt(index);

    /// <summary>アドレス領域 16 バイトを新しい配列にコピーして返す (ヒープ確保あり)。</summary>
    public byte[] AddressBytes()
    {
        var arr = new byte[AddressSize];
        _address.AsSpan().CopyTo(arr);
        return arr;
    }

    /// <summary>UDPv4 の場合は 4 バイトの IPv4 を返す。それ以外は <see cref="IPAddress.None"/>。</summary>
    public IPAddress ToIPAddress()
    {
        return Kind switch
        {
            LocatorKind.UdpV4 => new IPAddress(_address.AsSpan()[12..16].ToArray()),
            LocatorKind.UdpV6 => new IPAddress(_address.AsSpan().ToArray()),
            _ => IPAddress.None,
        };
    }

    public void WriteTo(Span<byte> destination, bool littleEndian)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException(
                $"Destination requires at least {Size} bytes.", nameof(destination));
        }
        if (littleEndian)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination, (int)Kind);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], Port);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(destination, (int)Kind);
            BinaryPrimitives.WriteUInt32BigEndian(destination[4..], Port);
        }
        ReadOnlySpan<byte> addr = _address.AsSpan();
        addr.CopyTo(destination[8..24]);
    }

    public static Locator Read(ReadOnlySpan<byte> source, bool littleEndian)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException(
                $"Source requires at least {Size} bytes.", nameof(source));
        }
        int kind = littleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(source)
            : BinaryPrimitives.ReadInt32BigEndian(source);
        uint port = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(source[4..])
            : BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        return new Locator((LocatorKind)kind, port, source.Slice(8, AddressSize));
    }

    public bool Equals(Locator other)
    {
        if (Kind != other.Kind || Port != other.Port)
        {
            return false;
        }
        ReadOnlySpan<byte> a = _address.AsSpan();
        ReadOnlySpan<byte> b = other._address.AsSpan();
        return a.SequenceEqual(b);
    }

    public override bool Equals(object? obj) => obj is Locator l && Equals(l);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add((int)Kind);
        hash.Add(Port);
        ReadOnlySpan<byte> bytes = _address.AsSpan();
        for (int i = 0; i < bytes.Length; i++)
            hash.Add(bytes[i]);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return Kind switch
        {
            LocatorKind.UdpV4 => $"UDPv4://{ToIPAddress()}:{Port}",
            LocatorKind.UdpV6 => $"UDPv6://[{ToIPAddress()}]:{Port}",
            LocatorKind.Invalid => "INVALID",
            _ => $"{Kind}://{HexUtil.ToHexString(_address.AsSpan())}:{Port}",
        };
    }

    public static bool operator ==(Locator left, Locator right) => left.Equals(right);
    public static bool operator !=(Locator left, Locator right) => !left.Equals(right);
}

/// <summary>
/// Locator のアドレス領域 (16 バイト) を値型として確保するストレージ。
/// アクセスは <see cref="LocatorAddressStorageExtensions"/> の <c>ref this</c> 拡張経由。
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = Locator.AddressSize)]
internal struct LocatorAddressStorage
{
    internal byte First;
}

internal static class LocatorAddressStorageExtensions
{
    public static Span<byte> AsSpan(ref this LocatorAddressStorage storage)
        => MemoryMarshal.CreateSpan(ref storage.First, Locator.AddressSize);

    public static byte ElementAt(ref this LocatorAddressStorage storage, int index)
        => storage.AsSpan()[index];
}
