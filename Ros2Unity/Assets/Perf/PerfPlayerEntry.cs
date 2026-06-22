using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.Cdr;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Msgs.Std;
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

                using (var metrics = new PerfMetricsWriter(parsed))
                {
                    try
                    {
                        metrics.Event("start");
                        using (PerfProfilerRecorders recorders = PerfProfilerRecorders.Start())
                        {
                            if (parsed.Direction == PerfDirection.UnityToRos2)
                            {
                                await RunUnityToRos2(parsed, metrics, recorders);
                            }
                            else
                            {
                                await RunRos2ToUnity(parsed, metrics, recorders);
                            }
                        }
                        await WaitForRelease(parsed, metrics);
                        metrics.WriteSentinel(parsed.DoneFile, "ok");
                        metrics.Event("done");
                        Quit(0);
                    }
                    catch (Exception ex)
                    {
                        metrics.Event("error", new Dictionary<string, object> { { "message", ex.ToString() } });
                        metrics.WriteSentinel(parsed.DoneFile, "error");
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

        private static async Task RunUnityToRos2(
            PerfPlayerArguments args,
            PerfMetricsWriter metrics,
            PerfProfilerRecorders recorders)
        {
            using (var participant = CreateParticipant(args.DomainId, "player_pub"))
            using (var publisher = participant.CreatePublisher<StringMessage>(
                       args.Topic,
                       StringMessageSerializer.Instance,
                       args.Reliability,
                       DurabilityQos.Volatile))
            {
                participant.Start();
                metrics.WriteSentinel(args.ReadyFile, "ready");
                metrics.Event("ready");

                bool matched = await publisher.WaitForMatchedAsync(1, TimeSpan.FromSeconds(10));
                if (!matched)
                {
                    throw new TimeoutException("Publisher did not match a ROS 2 subscriber.");
                }

                metrics.Event("matched");
                var message = CreatePayloadMessage(args.PayloadBytes);
                int serializedBytes = publisher.SerializeWithEncapsulation(message).Length;
                var stopwatch = Stopwatch.StartNew();
                metrics.Event("measure_start");
                for (int i = 0; i < args.Messages; i++)
                {
                    await publisher.PublishAsync(message);
                }
                stopwatch.Stop();

                Dictionary<string, object> fields = recorders.Snapshot();
                fields["elapsed_ms"] = stopwatch.Elapsed.TotalMilliseconds;
                fields["sent"] = args.Messages;
                fields["serialized_bytes_per_message"] = serializedBytes;
                fields["serialized_bytes"] = (long)serializedBytes * args.Messages;
                fields["messages_per_second"] = args.Messages / Math.Max(0.000001d, stopwatch.Elapsed.TotalSeconds);
                metrics.Event("measure_done", fields);
            }
        }

        private static async Task RunRos2ToUnity(
            PerfPlayerArguments args,
            PerfMetricsWriter metrics,
            PerfProfilerRecorders recorders)
        {
            int received = 0;
            Stopwatch stopwatch = null;
            using (var participant = CreateParticipant(args.DomainId, "player_sub"))
            using (var subscription = participant.CreateSubscription<StringMessage>(
                       args.Topic,
                       StringMessageSerializer.Instance,
                       _ =>
                       {
                           if (Interlocked.Increment(ref received) == 1)
                           {
                               stopwatch = Stopwatch.StartNew();
                           }
                       },
                       reliability: args.Reliability))
            {
                participant.Start();
                metrics.WriteSentinel(args.ReadyFile, "ready");
                metrics.Event("ready");

                bool matched = await subscription.WaitForMatchedAsync(1, TimeSpan.FromSeconds(20));
                if (!matched)
                {
                    throw new TimeoutException("Subscription did not match a ROS 2 publisher.");
                }
                metrics.Event("matched");
                metrics.Event("measure_start");

                bool completed = await AsyncReceiveWaiter.WaitUntilAsync(
                    () => Volatile.Read(ref received) >= args.Messages,
                    TimeSpan.FromSeconds(30),
                    async delay =>
                    {
                        recorders.Collect();
                        await Task.Delay(delay);
                    });
                if (!completed)
                {
                    throw new TimeoutException(
                        "Timed out waiting for ROS 2 messages: received " +
                        Volatile.Read(ref received) + "/" + args.Messages + ".");
                }
                stopwatch?.Stop();

                var message = CreatePayloadMessage(args.PayloadBytes);
                int serializedBytes = CdrEncapsulation.Size + StringMessageSerializer.Instance.GetSerializedSize(message);
                double elapsedMs = stopwatch == null ? 0.0d : stopwatch.Elapsed.TotalMilliseconds;
                Dictionary<string, object> fields = recorders.Snapshot();
                fields["elapsed_ms"] = elapsedMs;
                fields["received"] = received;
                fields["serialized_bytes_per_message"] = serializedBytes;
                fields["serialized_bytes"] = (long)serializedBytes * received;
                fields["messages_per_second"] = received / Math.Max(0.000001d, elapsedMs / 1000.0d);
                metrics.Event("measure_done", fields);
            }
        }

        private static DomainParticipant CreateParticipant(int domainId, string entityName)
            => new DomainParticipant(new DomainParticipantOptions
            {
                DomainId = domainId,
                ParticipantId = 0,
                EntityName = "rosettadds_perf_" + entityName,
                LocalhostOnly = true,
                SpdpInterval = TimeSpan.FromMilliseconds(100),
                SedpInterval = TimeSpan.FromMilliseconds(100),
                UserWriterHeartbeatPeriod = TimeSpan.FromMilliseconds(100),
            });

        private static async Task WaitForRelease(PerfPlayerArguments args, PerfMetricsWriter metrics)
        {
            if (string.IsNullOrEmpty(args.ReleaseFile))
            {
                return;
            }

            metrics.Event("waiting_for_release");
            var deadline = Stopwatch.StartNew();
            while (!System.IO.File.Exists(args.ReleaseFile) && deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(20);
            }
            if (!System.IO.File.Exists(args.ReleaseFile))
            {
                throw new TimeoutException("Timed out waiting for release file: " + args.ReleaseFile);
            }
            metrics.Event("released");
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
