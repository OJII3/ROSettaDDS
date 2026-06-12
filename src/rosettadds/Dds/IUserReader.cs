using ROSettaDDS.Common;
using ROSettaDDS.Rtps;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Dds;

/// <summary>
/// ユーザートピック Subscription が使う RTPS Reader の抽象。
/// Best-Effort (<see cref="BestEffortUserReader"/> = StatelessReader) と
/// Reliable (<see cref="ReliableUserReader"/> = StatefulReader) の差異を吸収する。
/// </summary>
internal interface IUserReader : IDisposable
{
    EntityId ReaderEntityId { get; }
    Guid Guid { get; }

    /// <summary><see cref="ParticipantRtpsReceiver"/> へ登録する submessage ハンドラ。</summary>
    IRtpsSubmessageHandler Handler { get; }

    /// <summary>
    /// マッチング DATA を受信したときに発火。第二引数は送信元 Participant の GuidPrefix。
    /// payload は呼び出し中のみ有効な場合があるため、保持する場合は呼び出し側で複製する。
    /// </summary>
    event Action<ReadOnlyMemory<byte>, GuidPrefix>? PayloadReceived;

    /// <summary>
    /// remote/local writer を受信対象に追加する。
    /// <paramref name="unicastReplyLocator"/> は Reliable 経路で ACKNACK を返す宛先
    /// (Best-Effort では無視される)。
    /// </summary>
    void MatchWriter(Guid writerGuid, Locator? unicastReplyLocator);
    void UnmatchWriter(Guid writerGuid);

    void Start();
    void Stop();
}
