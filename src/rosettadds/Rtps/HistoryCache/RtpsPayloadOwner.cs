using System.Buffers;
using System.Threading;

namespace ROSettaDDS.Rtps.HistoryCache;

internal sealed class RtpsPayloadOwner : IDisposable
{
    private byte[]? _buffer;

    internal RtpsPayloadOwner(byte[] buffer)
    {
        _buffer = buffer;
    }

    internal byte[] Buffer
    {
        get
        {
            var b = _buffer;
            if (b is null)
            {
                throw new ObjectDisposedException(nameof(RtpsPayloadOwner));
            }
            return b;
        }
    }

    public void Dispose()
    {
        var b = Interlocked.Exchange(ref _buffer, null);
        if (b is not null)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }
}
