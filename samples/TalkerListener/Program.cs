// rosettadds talker/listener サンプル (std_msgs/String, topic = /chatter)
// Usage:
//   dotnet run --project samples/TalkerListener -- talker   [domainId] [participantId] [entityName]
//   dotnet run --project samples/TalkerListener -- listener [domainId] [participantId] [entityName]
//
// ROS 2 との相互通信例 (別シェルで):
//   ROS_LOCALHOST_ONLY=1 ros2 run demo_nodes_cpp listener
//   ROS_LOCALHOST_ONLY=1 ros2 run demo_nodes_cpp talker
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rcl;
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

var options = new ContextOptions
{
    DomainId = domainId,
    ParticipantId = participantId,
    EntityName = entityName,
    Logger = logger,
    // ROS_LOCALHOST_ONLY=1 相当: unicast/multicast を loopback に限定する。
    LocalhostOnly = true,
    SpdpInterval = TimeSpan.FromSeconds(1),
};

using var context = new Context(options);
using var node = new Node(context, entityName);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

context.Start();
logger.Info($"Starting {mode}: domain={domainId} participant={participantId} name={entityName}");
logger.Info($"Local Guid: {context.Guid}");

if (mode == "talker")
{
    using var pub = node.CreatePublisher<StringMessage>(
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
    using var sub = node.CreateSubscription<StringMessage>(
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
context.Stop();
return 0;
