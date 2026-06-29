using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.Cdr;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Transport;
using UnityEngine;

namespace ROSettaDDS.UnityPerfHarness
{
    internal static class PerfPlayerEntry
    {
        private static int s_exitCode;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static async void StartIfRequested()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (!PerfPlayerArguments.IsPerfRun(args))
            {
                return;
            }

            await Run(args);
        }

        private static async Task Run(string[] args)
        {
            try
            {
                if (!PerfPlayerArguments.TryParse(args, out var parsed, out string error))
                {
                    UnityEngine.Debug.LogError(error);
                    Quit(1);
                    return;
                }

                using (var sink = new PerfMetricsWriter(parsed))
                {
                    try
                    {
                        sink.Event("start");
                        using (var sampler = parsed.ProfilerMode == PerfProfilerMode.Full
                            ? PerfProfilerRecorders.StartFull()
                            : PerfProfilerRecorders.StartLean())
                        {
                            IPerfClock clock = new StopwatchPerfClock();
                            if (parsed.Direction == PerfDirection.UnityToRos2)
                            {
                                using (var participant = CreateParticipant(parsed, "player_pub"))
                                {
                                    await RunUnityToRos2(parsed, participant, sink, sampler, clock);
                                }
                            }
                            else
                            {
                                using (var participant = CreateParticipant(parsed, "player_sub"))
                                {
                                    await RunRos2ToUnity(parsed, participant, sink, sampler, clock);
                                }
                            }
                        }
                        await WaitForRelease(parsed, sink);
                        sink.WriteSentinel(parsed.DoneFile, "ok");
                        sink.Event("done");
                        Quit(0);
                    }
                    catch (Exception ex)
                    {
                        sink.Event("error", new Dictionary<string, object> { { "message", ex.ToString() } });
                        sink.WriteSentinel(parsed.DoneFile, "error");
                        Quit(1);
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError(ex);
                Quit(1);
            }
        }

        internal static async Task RunUnityToRos2(
            PerfPlayerArguments args,
            DomainParticipant participant,
            IPerfMetricsSink sink,
            IPerfProfilerSampler sampler,
            IPerfClock clock)
        {
            using (var publisher = participant.CreatePublisher<StringMessage>(
                       args.Topic,
                       StringMessageSerializer.Instance,
                       args.Reliability,
                       DurabilityQos.Volatile))
            {
                participant.Start();
                sink.WriteSentinel(args.ReadyFile, "ready");
                sink.Event("ready");

                bool matched = await publisher.WaitForMatchedAsync(1, TimeSpan.FromSeconds(10));
                if (!matched)
                {
                    throw new TimeoutException("Publisher did not match a ROS 2 subscriber.");
                }

                sink.Event("matched");
                var message = CreatePayloadMessage(args.PayloadBytes);
                int serializedBytes = publisher.SerializeWithEncapsulation(message).Length;

                using (IPerfStopwatch sw = clock.Start())
                {
                    sink.Event("measure_start");
                    await publisher.PublishRepeatedAsync(message, args.Messages);
                    sw.Stop();

                    IDictionary<string, object> profilerFields = sampler.Snapshot();
                    IDictionary<string, object> fields = MeasureDoneBuilder.BuildPublish(
                        sw.Elapsed, args.Messages, serializedBytes, profilerFields);
                    sink.Event("measure_done", fields);
                }
            }
        }

        internal static async Task RunRos2ToUnity(
            PerfPlayerArguments args,
            DomainParticipant participant,
            IPerfMetricsSink sink,
            IPerfProfilerSampler sampler,
            IPerfClock clock)
        {
            int received = 0;
            IPerfStopwatch sw = null;
            using (var subscription = participant.CreateSubscription<StringMessage>(
                       args.Topic,
                       StringMessageSerializer.Instance,
                       _ =>
                       {
                           if (Interlocked.Increment(ref received) == 1)
                           {
                               sw = clock.Start();
                           }
                       },
                       reliability: args.Reliability))
            {
                participant.Start();
                sink.WriteSentinel(args.ReadyFile, "ready");
                sink.Event("ready");

                bool matched = await subscription.WaitForMatchedAsync(1, TimeSpan.FromSeconds(20));
                if (!matched)
                {
                    throw new TimeoutException("Subscription did not match a ROS 2 publisher.");
                }
                sink.Event("matched");
                sink.Event("measure_start");

                try
                {
                    bool completed = await AsyncReceiveWaiter.WaitUntilAsync(
                        () => Volatile.Read(ref received) >= args.Messages,
                        TimeSpan.FromSeconds(30),
                        async delay =>
                        {
                            sampler.Collect();
                            await Task.Delay(delay);
                        });
                    if (!completed)
                    {
                        sink.Event("receive_diagnostics", BuildReceiveDiagnostics(participant, subscription));
                        throw new TimeoutException(
                            "Timed out waiting for ROS 2 messages: received " +
                            Volatile.Read(ref received) + "/" + args.Messages + ".");
                    }
                    sw?.Stop();

                    var message = CreatePayloadMessage(args.PayloadBytes);
                    int serializedBytes = CdrEncapsulation.Size + StringMessageSerializer.Instance.GetSerializedSize(message);
                    IDictionary<string, object> profilerFields = sampler.Snapshot();
                    IDictionary<string, object> diagnostics = CollectReceiveDiagnostics(participant, subscription);
                    IDictionary<string, object> fields = MeasureDoneBuilder.BuildSubscribe(
                        sw?.Elapsed ?? TimeSpan.Zero, received, serializedBytes, profilerFields, diagnostics);
                    sink.Event("measure_done", fields);
                }
                finally
                {
                    sw?.Dispose();
                }
            }
        }

        private static IDictionary<string, object> BuildReceiveDiagnostics(
            DomainParticipant participant,
            Subscription<StringMessage> subscription)
        {
            return PerfDiagnosticsBuilder.BuildReceive(
                CollectTransport(participant.UserUnicastTransport),
                CollectTransport(participant.UserMulticastTransport),
                CollectSubscription(subscription));
        }

        private static IDictionary<string, object> CollectReceiveDiagnostics(
            DomainParticipant participant,
            Subscription<StringMessage> subscription)
        {
            return BuildReceiveDiagnostics(participant, subscription);
        }

        private static PerfTransportDiagnostics CollectTransport(IRtpsTransport transport)
        {
            if (transport is not UdpTransport udp)
            {
                return PerfTransportDiagnostics.Unavailable();
            }
            var d = udp.Diagnostics;
            return new PerfTransportDiagnostics(
                available: true,
                datagramsReceived: d.DatagramsReceived,
                datagramsEnqueued: d.DatagramsEnqueued,
                datagramsDropped: d.DatagramsDropped,
                datagramsDispatched: d.DatagramsDispatched,
                queueCount: d.QueueCount);
        }

        private static PerfSubscriptionDiagnostics CollectSubscription(Subscription<StringMessage> subscription)
        {
            var d = subscription.Diagnostics;
            var rtps = d.RtpsReader;
            return new PerfSubscriptionDiagnostics(
                payloadsReceivedFromReader: d.PayloadsReceivedFromReader,
                messagesDeserialized: d.MessagesDeserialized,
                deserializeFailures: d.DeserializeFailures,
                handlerInvocations: d.HandlerInvocations,
                dataSubmessagesReceived: rtps.DataSubmessagesReceived,
                dataFragSubmessagesReceived: rtps.DataFragSubmessagesReceived,
                reassembledPayloads: rtps.ReassembledPayloads,
                payloadsDelivered: rtps.PayloadsDelivered,
                payloadsBufferedPendingMatch: rtps.PayloadsBufferedPendingMatch,
                payloadsDropped: rtps.PayloadsDropped);
        }

        private static DomainParticipant CreateParticipant(
            PerfPlayerArguments args,
            string entityName)
            => new DomainParticipant(new DomainParticipantOptions
            {
                DomainId = args.DomainId,
                ParticipantId = 0,
                EntityName = "rosettadds_perf_" + entityName,
                LocalhostOnly = args.LocalhostOnly,
                SpdpInterval = TimeSpan.FromMilliseconds(100),
                SedpInterval = TimeSpan.FromMilliseconds(100),
                UserWriterHeartbeatPeriod = TimeSpan.FromMilliseconds(100),
            });

        private static async Task WaitForRelease(PerfPlayerArguments args, IPerfMetricsSink sink)
        {
            if (string.IsNullOrEmpty(args.ReleaseFile))
            {
                return;
            }

            sink.Event("waiting_for_release");
            var deadline = Stopwatch.StartNew();
            while (!System.IO.File.Exists(args.ReleaseFile) && deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(20);
            }
            if (!System.IO.File.Exists(args.ReleaseFile))
            {
                throw new TimeoutException("Timed out waiting for release file: " + args.ReleaseFile);
            }
            sink.Event("released");
        }

        private static StringMessage CreatePayloadMessage(int payloadBytes)
        {
            const string prefix = "unity-player-perf-";
            string data = prefix + new string('x', Math.Max(1, payloadBytes - prefix.Length));
            return new StringMessage(data);
        }

        private static void Quit(int exitCode)
        {
            s_exitCode = exitCode;
            Application.Quit(exitCode);
        }
    }
}
