using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ROSettaDDS.Common;

/// <summary>
/// RTPS GuidPrefix (12 バイト固定)。RTPS 仕様 8.2.4.3。
/// rosettadds のレイアウト:
/// - bytes 0-1: VendorId
/// - bytes 2-5: hostId (BE uint32)
/// - bytes 6-9: processId (BE uint32)
/// - bytes 10-11: instanceCounter (BE uint16)
/// </summary>
public struct GuidPrefix : IEquatable<GuidPrefix>
{
    public const int Size = 12;

    private GuidPrefixStorage _storage;

    public GuidPrefix(ReadOnlySpan<byte> source)
    {
        if (source.Length != Size)
        {
            throw new ArgumentException(
                $"GuidPrefix requires exactly {Size} bytes, got {source.Length}.", nameof(source));
        }
        _storage = default;
        source.CopyTo(_storage.AsSpan());
    }

    /// <summary>すべて 0 の Unknown GuidPrefix。</summary>
    public static GuidPrefix Unknown => default;

    /// <summary>明示的に各フィールドを指定して GuidPrefix を生成する。</summary>
    public static GuidPrefix Create(VendorId vendorId, uint hostId, uint processId, ushort instanceCounter)
    {
        Span<byte> bytes = stackalloc byte[Size];
        bytes[0] = vendorId.V0;
        bytes[1] = vendorId.V1;
        BinaryPrimitives.WriteUInt32BigEndian(bytes[2..6], hostId);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[6..10], processId);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[10..12], instanceCounter);
        return new GuidPrefix(bytes);
    }

    private static int s_instanceCounter;
    private static readonly uint s_processSeed = unchecked((uint)new Random().Next());
    private static readonly uint s_cachedPid = GetCurrentProcessId();

    private static uint GetCurrentProcessId()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return unchecked((uint)p.Id);
    }

    /// <summary>
    /// 現在のプロセス固有の GuidPrefix を生成する。
    /// hostId は <see cref="Environment.MachineName"/> のハッシュ、processId は実 PID と
    /// プロセス起動時の乱数シードを XOR したもの、instanceCounter はプロセス内連番。
    /// </summary>
    public static GuidPrefix CreateForCurrentProcess(VendorId vendorId)
    {
        uint hostId = unchecked((uint)Environment.MachineName.GetHashCode());
        uint pid = s_cachedPid ^ s_processSeed;
        ushort counter = unchecked((ushort)Interlocked.Increment(ref s_instanceCounter));
        return Create(vendorId, hostId, pid, counter);
    }

    public byte this[int index] => _storage.ElementAt(index);

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException(
                $"Destination requires at least {Size} bytes.", nameof(destination));
        }
        ReadOnlySpan<byte> source = _storage.AsSpan();
        source.CopyTo(destination);
    }

    public byte[] ToByteArray()
    {
        var arr = new byte[Size];
        CopyTo(arr);
        return arr;
    }

    public bool Equals(GuidPrefix other)
    {
        ReadOnlySpan<byte> a = _storage.AsSpan();
        ReadOnlySpan<byte> b = other._storage.AsSpan();
        return a.SequenceEqual(b);
    }

    public override bool Equals(object? obj) => obj is GuidPrefix gp && Equals(gp);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        ReadOnlySpan<byte> bytes = _storage.AsSpan();
        for (int i = 0; i < bytes.Length; i++)
            hash.Add(bytes[i]);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        ReadOnlySpan<byte> bytes = _storage.AsSpan();
        return HexUtil.ToHexString(bytes);
    }

    public static bool operator ==(GuidPrefix left, GuidPrefix right) => left.Equals(right);
    public static bool operator !=(GuidPrefix left, GuidPrefix right) => !left.Equals(right);
}

/// <summary>
/// GuidPrefix の 12 バイト固定ストレージ。
/// 値型として埋め込むため <see cref="StructLayoutAttribute"/> の <c>Size</c> で領域を確保する。
/// Span アクセスは <see cref="GuidPrefixStorageExtensions"/> のメソッド経由で行う
/// (<c>ref this</c> extension にすることで <c>[UnscopedRef]</c> 非対応の C# 9/10 コンパイラでも通る)。
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = GuidPrefix.Size)]
internal struct GuidPrefixStorage
{
    internal byte First;
}

internal static class GuidPrefixStorageExtensions
{
    public static Span<byte> AsSpan(ref this GuidPrefixStorage storage)
        => MemoryMarshal.CreateSpan(ref storage.First, GuidPrefix.Size);

    public static byte ElementAt(ref this GuidPrefixStorage storage, int index)
        => storage.AsSpan()[index];
}
