using Rclsharp.Common;

namespace Rclsharp.Rcl.Naming;

/// <summary>
/// ユーザートピック用の EntityId を Participant 内の小さい連番で割り当てる。
/// Fast DDS などの ROS 2 実装が広告する user endpoint id に合わせ、
/// entityKey は 0x000005 から始める。
/// </summary>
public sealed class UserEntityIdAllocator
{
    public const uint FirstUserEntityKey = 0x000005u;

    private readonly object _lock = new();
    private uint _nextWriterKey;
    private uint _nextReaderKey;

    public UserEntityIdAllocator(uint firstUserEntityKey = FirstUserEntityKey)
    {
        if (firstUserEntityKey == 0 || firstUserEntityKey > 0x00FF_FFFFu)
        {
            throw new ArgumentOutOfRangeException(nameof(firstUserEntityKey),
                "First entity key must fit in 24 bits and be greater than zero.");
        }
        _nextWriterKey = firstUserEntityKey;
        _nextReaderKey = firstUserEntityKey;
    }

    public EntityId AllocateWriter()
        => Allocate(ref _nextWriterKey, EntityKind.UserDefinedWriterNoKey);

    public EntityId AllocateReader()
        => Allocate(ref _nextReaderKey, EntityKind.UserDefinedReaderNoKey);

    private EntityId Allocate(ref uint nextKey, EntityKind kind)
    {
        lock (_lock)
        {
            if (nextKey > 0x00FF_FFFFu)
            {
                throw new InvalidOperationException("No user EntityId keys remain in the 24-bit RTPS entity key space.");
            }
            var id = new EntityId(nextKey, kind);
            nextKey++;
            return id;
        }
    }
}
