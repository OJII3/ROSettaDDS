using System.Collections.Generic;

namespace ROSettaDDS.UnityPerfHarness
{
    internal static class PerfDiagnosticsBuilder
    {
        internal static IDictionary<string, object> BuildReceive(
            PerfTransportDiagnostics userUnicast,
            PerfTransportDiagnostics userMulticast,
            PerfSubscriptionDiagnostics subscription)
        {
            var fields = new Dictionary<string, object>();
            AddTransport(fields, "user_unicast", userUnicast);
            AddTransport(fields, "user_multicast", userMulticast);

            fields["subscription_payloads_from_reader"] = subscription.PayloadsReceivedFromReader;
            fields["subscription_messages_deserialized"] = subscription.MessagesDeserialized;
            fields["subscription_deserialize_failures"] = subscription.DeserializeFailures;
            fields["subscription_handler_invocations"] = subscription.HandlerInvocations;
            fields["rtps_data_submessages_received"] = subscription.DataSubmessagesReceived;
            fields["rtps_datafrag_submessages_received"] = subscription.DataFragSubmessagesReceived;
            fields["rtps_reassembled_payloads"] = subscription.ReassembledPayloads;
            fields["rtps_payloads_delivered"] = subscription.PayloadsDelivered;
            fields["rtps_payloads_buffered_pending_match"] = subscription.PayloadsBufferedPendingMatch;
            fields["rtps_payloads_dropped"] = subscription.PayloadsDropped;
            return fields;
        }

        private static void AddTransport(
            IDictionary<string, object> fields,
            string prefix,
            PerfTransportDiagnostics transport)
        {
            fields[prefix + "_transport_diagnostics_available"] = transport.Available;
            if (!transport.Available) return;
            fields[prefix + "_udp_datagrams_received"] = transport.DatagramsReceived;
            fields[prefix + "_udp_datagrams_enqueued"] = transport.DatagramsEnqueued;
            fields[prefix + "_udp_datagrams_dropped"] = transport.DatagramsDropped;
            fields[prefix + "_udp_datagrams_dispatched"] = transport.DatagramsDispatched;
            fields[prefix + "_udp_queue_count"] = transport.QueueCount;
        }
    }
}
