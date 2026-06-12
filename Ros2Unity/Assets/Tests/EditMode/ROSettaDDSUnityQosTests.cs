using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Msgs.Std;

namespace ROSettaDDS.UnityVerification.Tests
{
    public sealed class ROSettaDDSUnityQosTests
    {
        [Test]
        public void Reliable_DATAをdropしても全件再送される()
        {
            LossyTransport lossy = null;
            using var pair = UnityLoopbackTestSupport.CreatePair(
                inner => lossy = new LossyTransport(inner, 1, RtpsPacketPredicates.ContainsUserData));
            string topic = UnityLoopbackTestSupport.UniqueTopic("unity_reliable_loss");
            var received = new List<string>();
            var gate = new object();

            using var sub = pair.Reader.CreateSubscription<StringMessage>(
                topic,
                StringMessageSerializer.Instance,
                (message, _) =>
                {
                    lock (gate)
                    {
                        received.Add(message.Data);
                    }
                },
                reliability: ReliabilityQos.Reliable);
            using var pub = pair.Writer.CreatePublisher<StringMessage>(
                topic,
                StringMessageSerializer.Instance,
                ReliabilityQos.Reliable,
                DurabilityQos.Volatile);

            pair.Start();
            UnityLoopbackTestSupport.AssertDiscovered(pair, topic);

            var expected = Enumerable.Range(0, 8).Select(i => "reliable-" + i).ToArray();
            for (int i = 0; i < expected.Length; i++)
            {
                pub.PublishAsync(new StringMessage(expected[i])).GetAwaiter().GetResult();
            }

            Assert.IsTrue(
                UnityLoopbackTestSupport.WaitUntil(
                    () =>
                    {
                        lock (gate)
                        {
                            return received.Count == expected.Length;
                        }
                    },
                    UnityLoopbackTestSupport.ReceiveTimeout),
                "Reliable reader did not receive all messages after DATA loss.");

            Assert.AreEqual(1, lossy.DroppedCount);
            lock (gate)
            {
                CollectionAssert.AreEquivalent(expected, received);
            }
        }

        [Test]
        public void TransientLocalの履歴をlate_join_readerが受信できる()
        {
            using var pair = UnityLoopbackTestSupport.CreatePair();
            string topic = UnityLoopbackTestSupport.UniqueTopic("unity_transient_local");
            using var pub = pair.Writer.CreatePublisher<StringMessage>(
                topic,
                StringMessageSerializer.Instance,
                ReliabilityQos.Reliable,
                DurabilityQos.TransientLocal);

            pair.Start();
            pub.PublishAsync(new StringMessage("published-before-reader")).GetAwaiter().GetResult();

            string received = null;
            using var sub = pair.Reader.CreateSubscription<StringMessage>(
                topic,
                StringMessageSerializer.Instance,
                (message, _) => Volatile.Write(ref received, message.Data),
                reliability: ReliabilityQos.Reliable);

            Assert.IsTrue(
                UnityLoopbackTestSupport.WaitUntil(
                    () => Volatile.Read(ref received) == "published-before-reader",
                    UnityLoopbackTestSupport.ReceiveTimeout),
                "Late-join reader did not receive TransientLocal history.");
        }

        [Test]
        public void BestEffort_DATAが欠落しても後続publish_receiveを継続できる()
        {
            LossyTransport lossy = null;
            using var pair = UnityLoopbackTestSupport.CreatePair(
                inner => lossy = new LossyTransport(inner, 2, RtpsPacketPredicates.ContainsUserData));
            string topic = UnityLoopbackTestSupport.UniqueTopic("unity_best_effort_loss");
            var received = new List<string>();
            var gate = new object();

            using var sub = pair.Reader.CreateSubscription<StringMessage>(
                topic,
                StringMessageSerializer.Instance,
                (message, _) =>
                {
                    lock (gate)
                    {
                        received.Add(message.Data);
                    }
                },
                reliability: ReliabilityQos.BestEffort);
            using var pub = pair.Writer.CreatePublisher<StringMessage>(
                topic,
                StringMessageSerializer.Instance,
                ReliabilityQos.BestEffort,
                DurabilityQos.Volatile);

            pair.Start();
            UnityLoopbackTestSupport.AssertDiscovered(pair, topic);

            for (int i = 0; i < 6; i++)
            {
                pub.PublishAsync(new StringMessage("best-effort-" + i)).GetAwaiter().GetResult();
            }
            pub.PublishAsync(new StringMessage("best-effort-continued")).GetAwaiter().GetResult();

            Assert.IsTrue(
                UnityLoopbackTestSupport.WaitUntil(
                    () =>
                    {
                        lock (gate)
                        {
                            return received.Count == 5;
                        }
                    },
                    UnityLoopbackTestSupport.ReceiveTimeout),
                "BestEffort reader did not continue after packet loss.");

            Assert.AreEqual(2, lossy.DroppedCount);
            lock (gate)
            {
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "best-effort-2",
                        "best-effort-3",
                        "best-effort-4",
                        "best-effort-5",
                        "best-effort-continued",
                    },
                    received);
            }
        }
    }
}
