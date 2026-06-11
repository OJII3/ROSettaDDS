using System;
using System.Collections;
using System.Net;
using System.Threading;
using NUnit.Framework;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Transport;
using UnityEngine;
using UnityEngine.TestTools;

namespace ROSettaDDS.UnityPlayMode.Tests
{
    public sealed class ROSettaDDSUdpLifecyclePlayModeTests
    {
        [UnityTest]
        public IEnumerator MonoBehaviour_lifecycleで実UDP_pubsubを開始停止できる()
        {
            var go = new GameObject("rosettadds-udp-lifecycle-probe");
            var probe = go.AddComponent<ROSettaDDSUdpLifecycleProbe>();

            yield return WaitUntil(() => probe.IsRunning || probe.Error != null, TimeSpan.FromSeconds(5));
            Assert.IsNull(probe.Error);
            Assert.IsTrue(probe.IsRunning);

            yield return PublishUntilReceived(probe, "first");
            Assert.AreEqual("first", probe.LastReceived);

            go.SetActive(false);
            yield return null;
            Assert.IsFalse(probe.IsRunning);
            Assert.AreEqual(1, probe.DisableCount);

            go.SetActive(true);
            yield return WaitUntil(() => probe.IsRunning || probe.Error != null, TimeSpan.FromSeconds(5));
            Assert.IsNull(probe.Error);

            yield return PublishUntilReceived(probe, "second");
            Assert.AreEqual("second", probe.LastReceived);

            UnityEngine.Object.Destroy(go);
            yield return null;
            Assert.GreaterOrEqual(ROSettaDDSUdpLifecycleProbe.DestroyCount, 1);
        }

        private static IEnumerator PublishUntilReceived(ROSettaDDSUdpLifecycleProbe probe, string value)
        {
            var deadline = UnityEngine.Time.realtimeSinceStartup + 8f;
            while (UnityEngine.Time.realtimeSinceStartup < deadline)
            {
                Assert.IsNull(probe.Error);
                probe.Publish(value);
                if (probe.LastReceived == value)
                {
                    yield break;
                }
                yield return new WaitForSeconds(0.05f);
            }

            Assert.AreEqual(value, probe.LastReceived);
        }

        private static IEnumerator WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = UnityEngine.Time.realtimeSinceStartup + (float)timeout.TotalSeconds;
            while (UnityEngine.Time.realtimeSinceStartup < deadline)
            {
                if (condition())
                {
                    yield break;
                }
                yield return null;
            }
        }
    }

    public sealed class ROSettaDDSUdpLifecycleProbe : MonoBehaviour
    {
        private const string TopicName = "unity_playmode_udp_lifecycle";
        private static int s_sequence;

        private DomainParticipant _writerParticipant;
        private DomainParticipant _readerParticipant;
        private Publisher<StringMessage> _publisher;
        private Subscription<StringMessage> _subscription;
        private string _lastReceived;

        public bool IsRunning { get; private set; }
        public Exception Error { get; private set; }
        public int DisableCount { get; private set; }
        public static int DestroyCount { get; private set; }
        public string LastReceived => Volatile.Read(ref _lastReceived);

        private void OnEnable()
        {
            try
            {
                StartParticipants();
            }
            catch (Exception ex)
            {
                Error = ex;
            }
        }

        private void OnDisable()
        {
            DisableCount++;
            StopParticipants();
        }

        private void OnDestroy()
        {
            DestroyCount++;
            StopParticipants();
        }

        public void Publish(string value)
        {
            if (_publisher == null)
            {
                return;
            }

            _publisher.PublishAsync(new StringMessage(value)).GetAwaiter().GetResult();
        }

        private void StartParticipants()
        {
            StopParticipants();
            _lastReceived = null;
            Error = null;

            int sequence = Interlocked.Increment(ref s_sequence);
            int domainId = 100 + (sequence % 20);
            int writerParticipantId = 70;
            int readerParticipantId = 71;
            var multicastGroup = IPAddress.Parse("239.255.0.1");

            _writerParticipant = new DomainParticipant(new DomainParticipantOptions
            {
                DomainId = domainId,
                ParticipantId = writerParticipantId,
                EntityName = "unity_playmode_udp_writer_" + sequence,
                LocalUnicastAddress = IPAddress.Loopback,
                MulticastInterface = IPAddress.Loopback,
                MulticastGroup = multicastGroup,
                SpdpInterval = TimeSpan.FromMilliseconds(50),
                SedpInterval = TimeSpan.FromMilliseconds(50),
                LeaseDuration = Duration.FromSeconds(2),
            });

            _readerParticipant = new DomainParticipant(new DomainParticipantOptions
            {
                DomainId = domainId,
                ParticipantId = readerParticipantId,
                EntityName = "unity_playmode_udp_reader_" + sequence,
                LocalUnicastAddress = IPAddress.Loopback,
                MulticastInterface = IPAddress.Loopback,
                MulticastGroup = multicastGroup,
                SpdpInterval = TimeSpan.FromMilliseconds(50),
                SedpInterval = TimeSpan.FromMilliseconds(50),
                LeaseDuration = Duration.FromSeconds(2),
            });

            _subscription = _readerParticipant.CreateSubscription<StringMessage>(
                TopicName,
                StringMessageSerializer.Instance,
                message => Volatile.Write(ref _lastReceived, message.Data),
                handlerContext: null);

            _publisher = _writerParticipant.CreatePublisher<StringMessage>(
                TopicName,
                StringMessageSerializer.Instance,
                typeName: StringMessage.DdsTypeName);

            _writerParticipant.Start();
            _readerParticipant.Start();
            IsRunning = true;
        }

        private void StopParticipants()
        {
            IsRunning = false;

            try
            {
                if (_publisher != null)
                {
                    _publisher.Dispose();
                    _publisher = null;
                }
                if (_subscription != null)
                {
                    _subscription.Dispose();
                    _subscription = null;
                }
                if (_writerParticipant != null)
                {
                    _writerParticipant.Dispose();
                    _writerParticipant = null;
                }
                if (_readerParticipant != null)
                {
                    _readerParticipant.Dispose();
                    _readerParticipant = null;
                }
            }
            catch (Exception ex)
            {
                Error = ex;
            }
        }
    }
}
