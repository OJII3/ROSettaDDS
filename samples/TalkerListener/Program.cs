// rosettadds talker/listener サンプル (std_msgs/String, topic = /chatter)
// Usage:
//   dotnet run --project samples/TalkerListener -- talker   [domainId] [participantId] [entityName]
//   dotnet run --project samples/TalkerListener -- listener [domainId] [participantId] [entityName]
//
// ROS 2 との相互通信例 (別シェルで):
//   ROS_LOCALHOST_ONLY=1 ros2 run demo_nodes_cpp listener
//   ROS_LOCALHOST_ONLY=1 ros2 run demo_nodes_cpp talker
using System.Net;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Msgs.Std;

if (args.Length < 1 || (args[0] != "talker" && args[0] != "listener"))
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project samples/TalkerListener -- <talker|listener> [domainId] [participantId] [entityName]");
    return 1;
}

string mode = args[0];
int domainId = args.Length > 1 ? int.Parse(args[1]) : 0;
int participantId = args.Length > 2 ? int.Parse(args[2]) : (mode == "talker" ? 1 : 2);
string entityName = args.Length > 3 ? args[3] : $"rosettadds_{mode}_{Environment.ProcessId}";

var logger = new ConsoleLogger(mode, LogLevel.Info);

var options = new DomainParticipantOptions
{
    DomainId = domainId,
    ParticipantId = participantId,
    EntityName = entityName,
    Logger = logger,
    LocalUnicastAddress = IPAddress.Loopback,
    MulticastInterface = IPAddress.Loopback,
    SpdpInterval = TimeSpan.FromSeconds(1),
};

using var participant = new DomainParticipant(options);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

participant.Start();
logger.Info($"Starting {mode}: domain={domainId} participant={participantId} name={entityName}");
logger.Info($"Local Guid: {participant.Guid}");

if (mode == "talker")
{
    using var pub = participant.CreatePublisher<StringMessage>(
        "chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName);

    int counter = 0;
    try
    {
        while (!cts.IsCancellationRequested)
        {
            var msg = new StringMessage($"Hello rosettadds: {++counter}");
            await pub.PublishAsync(msg, cts.Token);
            logger.Info($"Publishing: '{msg.Data}'");
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
        }
    }
    catch (OperationCanceledException) { }
}
else // listener
{
    using var sub = participant.CreateSubscription<StringMessage>(
        "chatter",
        StringMessageSerializer.Instance,
        (msg, src) => logger.Info($"I heard: '{msg.Data}' from {src}"),
        StringMessage.DdsTypeName);

    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException) { }
}

logger.Info("Stopping...");
participant.Stop();
return 0;
