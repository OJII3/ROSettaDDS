// rosettadds SPDP demo
// Usage: dotnet run --project samples/SpdpDemo -- [domainId] [participantId] [entityName]
// 例: dotnet run --project samples/SpdpDemo -- 0 1 my_node
//
// 動作:
// - 指定ドメインの SPDP マルチキャスト (239.255.0.1:7400+250*domain) に参加
// - ParticipantData を 3 秒周期で送信
// - 他の Participant (rosettadds 同士、または ROS 2 ノード) を検出してログ出力
//
// Wireshark で 239.255.0.1 / udp.port == 7400 をキャプチャすると送受信パケットを確認可能。
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;

int domainId = args.Length > 0 ? int.Parse(args[0]) : 0;
int participantId = args.Length > 1 ? int.Parse(args[1]) : 1;
string entityName = args.Length > 2 ? args[2] : $"rosettadds_demo_{Environment.ProcessId}";

var logger = new ConsoleLogger("SpdpDemo", LogLevel.Info);

var options = new DomainParticipantOptions
{
    DomainId = domainId,
    ParticipantId = participantId,
    EntityName = entityName,
    Logger = logger,
    // ROS_LOCALHOST_ONLY=1 相当: unicast/multicast を loopback に限定する。
    LocalhostOnly = true,
    SpdpInterval = TimeSpan.FromSeconds(3),
};

using var participant = new DomainParticipant(options);

participant.DiscoveryDb.ParticipantDiscovered += rp =>
    logger.Info($"++ DISCOVERED {rp.Data.EntityName ?? "<unnamed>"} guid={rp.Guid} unicast={string.Join(',', rp.Data.MetatrafficUnicastLocators)}");

participant.DiscoveryDb.ParticipantUpdated += rp =>
    logger.Trace($"~~ UPDATED    {rp.Data.EntityName ?? "<unnamed>"} guid={rp.Guid}");

participant.DiscoveryDb.ParticipantLost += rp =>
    logger.Info($"-- LOST       {rp.Data.EntityName ?? "<unnamed>"} guid={rp.Guid}");

logger.Info($"Starting SPDP: domain={domainId} participant={participantId} name={entityName}");
logger.Info($"Local Guid:    {participant.Guid}");
logger.Info($"Multicast:     {options.MulticastGroup}:{ROSettaDDS.Transport.RtpsPorts.DiscoveryMulticast(domainId)}");

participant.Start();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

logger.Info("Press Ctrl+C to stop.");
try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException) { }

logger.Info("Stopping...");
participant.Stop();
