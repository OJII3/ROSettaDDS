using System;
using System.Collections.Generic;
using NUnit.Framework;
using ROSettaDDS.UnityPerfHarness;

namespace ROSettaDDS.UnityVerification.Tests
{
    public sealed class MeasureDoneBuilderTests
    {
        [Test]
        public void BuildPublish_emits_expected_fields()
        {
            var profiler = new Dictionary<string, object>
            {
                { "main_thread_time_ns_available", true },
                { "main_thread_time_ns_last", 12345L },
            };
            var fields = MeasureDoneBuilder.BuildPublish(
                TimeSpan.FromSeconds(1),
                sent: 1000,
                serializedBytesPerMessage: 128,
                profilerFields: profiler);

            Assert.AreEqual(1000.0d, fields["sent"]);
            Assert.AreEqual(1000.0d, fields["elapsed_ms"]);
            Assert.AreEqual(128L, fields["serialized_bytes_per_message"]);
            Assert.AreEqual(128000L, fields["serialized_bytes"]);
            Assert.AreEqual(1000.0d, fields["messages_per_second"]);
            Assert.AreEqual(true, fields["main_thread_time_ns_available"]);
            Assert.AreEqual(12345L, fields["main_thread_time_ns_last"]);
        }

        [Test]
        public void BuildPublish_with_zero_elapsed_does_not_divide_by_zero()
        {
            var fields = MeasureDoneBuilder.BuildPublish(
                TimeSpan.Zero,
                sent: 1,
                serializedBytesPerMessage: 64,
                profilerFields: new Dictionary<string, object>());

            double mps = (double)fields["messages_per_second"];
            Assert.IsFalse(double.IsNaN(mps));
            Assert.IsFalse(double.IsInfinity(mps));
            Assert.Greater(mps, 0);
        }

        [Test]
        public void BuildPublish_preserves_all_profiler_fields()
        {
            var profiler = new Dictionary<string, object>
            {
                { "main_thread_time_ns_available", true },
                { "main_thread_time_ns_last", 100L },
                { "gc_used_memory_bytes_available", true },
                { "gc_used_memory_bytes_last", 200L },
                { "gc_allocated_in_frame_bytes_available", true },
                { "gc_allocated_in_frame_bytes_last", 300L },
                { "gc_allocated_in_frame_bytes_total", 400L },
                { "gc_allocated_in_frame_bytes_samples", 5L },
            };
            var fields = MeasureDoneBuilder.BuildPublish(
                TimeSpan.FromSeconds(2),
                sent: 100,
                serializedBytesPerMessage: 256,
                profilerFields: profiler);

            Assert.AreEqual(8, profiler.Count);
            foreach (var kv in profiler)
            {
                Assert.IsTrue(fields.ContainsKey(kv.Key), "missing key: " + kv.Key);
                Assert.AreEqual(kv.Value, fields[kv.Key]);
            }
        }

        [Test]
        public void BuildPublish_does_not_mutate_input_profiler_fields()
        {
            var profiler = new Dictionary<string, object>
            {
                { "main_thread_time_ns_available", true },
            };
            int beforeCount = profiler.Count;

            MeasureDoneBuilder.BuildPublish(
                TimeSpan.FromSeconds(1),
                sent: 10,
                serializedBytesPerMessage: 16,
                profilerFields: profiler);

            Assert.AreEqual(beforeCount, profiler.Count);
        }

        [Test]
        public void BuildPublish_zero_count_serialized_bytes_is_zero()
        {
            var fields = MeasureDoneBuilder.BuildPublish(
                TimeSpan.FromSeconds(1),
                sent: 0,
                serializedBytesPerMessage: 128,
                profilerFields: new Dictionary<string, object>());

            Assert.AreEqual(0L, fields["serialized_bytes"]);
            Assert.AreEqual(0L, fields["sent"]);
        }
    }
}
