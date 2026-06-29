using NUnit.Framework;
using ROSettaDDS.UnityPerfHarness;

namespace ROSettaDDS.UnityVerification.Tests
{
    public sealed class PerfDiagnosticsBuilderTests
    {
        private static PerfTransportDiagnostics MakeUdp(
            long received = 0, long enqueued = 0, long dropped = 0,
            long dispatched = 0, long queue = 0)
            => new PerfTransportDiagnostics(true, received, enqueued, dropped, dispatched, queue);

        private static PerfSubscriptionDiagnostics MakeSub(
            long payloads = 0, long deserialized = 0, long failures = 0,
            long handlerInvocations = 0, long dataSubmsg = 0, long dataFragSubmsg = 0,
            long reassembled = 0, long delivered = 0, long buffered = 0, long dropped = 0)
            => new PerfSubscriptionDiagnostics(
                payloads, deserialized, failures, handlerInvocations,
                dataSubmsg, dataFragSubmsg, reassembled, delivered, buffered, dropped);

        [Test]
        public void BuildReceive_all_available_emits_all_keys()
        {
            var userUnicast = MakeUdp(received: 100);
            var userMulticast = MakeUdp(received: 50);
            var sub = MakeSub(deserialized: 200);

            var fields = PerfDiagnosticsBuilder.BuildReceive(userUnicast, userMulticast, sub);

            // 期待: 2 transport × 6 + 10 subscription = 22 キー
            Assert.AreEqual(22, fields.Count);
            Assert.AreEqual(true, fields["user_unicast_transport_diagnostics_available"]);
            Assert.AreEqual(true, fields["user_multicast_transport_diagnostics_available"]);
            Assert.AreEqual(100L, fields["user_unicast_udp_datagrams_received"]);
            Assert.AreEqual(50L, fields["user_multicast_udp_datagrams_received"]);
            Assert.AreEqual(200L, fields["subscription_messages_deserialized"]);
        }

        [Test]
        public void BuildReceive_user_unicast_unavailable_emits_only_transport_diagnostics_available_false()
        {
            var userUnicast = PerfTransportDiagnostics.Unavailable();
            var userMulticast = MakeUdp(received: 10);
            var sub = MakeSub();

            var fields = PerfDiagnosticsBuilder.BuildReceive(userUnicast, userMulticast, sub);

            Assert.AreEqual(false, fields["user_unicast_transport_diagnostics_available"]);
            // user_unicast_udp_* キーは出ない
            Assert.IsFalse(fields.ContainsKey("user_unicast_udp_datagrams_received"));
            Assert.IsFalse(fields.ContainsKey("user_unicast_udp_datagrams_enqueued"));
            // user_multicast は出る
            Assert.AreEqual(10L, fields["user_multicast_udp_datagrams_received"]);
        }

        [Test]
        public void BuildReceive_propagates_subscription_values()
        {
            var sub = MakeSub(
                payloads: 1, deserialized: 2, failures: 3, handlerInvocations: 4,
                dataSubmsg: 5, dataFragSubmsg: 6, reassembled: 7, delivered: 8,
                buffered: 9, dropped: 10);

            var fields = PerfDiagnosticsBuilder.BuildReceive(
                PerfTransportDiagnostics.Unavailable(),
                PerfTransportDiagnostics.Unavailable(),
                sub);

            Assert.AreEqual(1L, fields["subscription_payloads_from_reader"]);
            Assert.AreEqual(2L, fields["subscription_messages_deserialized"]);
            Assert.AreEqual(3L, fields["subscription_deserialize_failures"]);
            Assert.AreEqual(4L, fields["subscription_handler_invocations"]);
            Assert.AreEqual(5L, fields["rtps_data_submessages_received"]);
            Assert.AreEqual(6L, fields["rtps_datafrag_submessages_received"]);
            Assert.AreEqual(7L, fields["rtps_reassembled_payloads"]);
            Assert.AreEqual(8L, fields["rtps_payloads_delivered"]);
            Assert.AreEqual(9L, fields["rtps_payloads_buffered_pending_match"]);
            Assert.AreEqual(10L, fields["rtps_payloads_dropped"]);
        }

        [Test]
        public void BuildReceive_expected_key_set_when_all_unavailable()
        {
            var fields = PerfDiagnosticsBuilder.BuildReceive(
                PerfTransportDiagnostics.Unavailable(),
                PerfTransportDiagnostics.Unavailable(),
                MakeSub());

            // 期待: 2 transport_diagnostics_available + 10 subscription = 12 キー
            Assert.AreEqual(12, fields.Count);
        }
    }
}
