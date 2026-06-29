using System;
using System.Threading.Tasks;
using NUnit.Framework;
using ROSettaDDS.UnityPerfHarness;

namespace ROSettaDDS.UnityVerification.Tests
{
    public sealed class PerfHarnessRegressionTests
    {
        [Test]
        public void GcAllocatedAccumulator_keeps_values_after_source_buffer_is_reused()
        {
            var accumulator = new ProfilerCounterAccumulator();
            long[] samples = { 1L, 2L, 3L };

            accumulator.Add(samples);
            samples[0] = 10L;
            samples[1] = 20L;
            samples[2] = 30L;
            accumulator.Add(samples);

            Assert.AreEqual(66L, accumulator.Total);
            Assert.AreEqual(6, accumulator.Samples);
            Assert.AreEqual(30L, accumulator.LastValue);
        }

        [Test]
        public async Task ReceiveWaiter_waits_with_async_delay_until_condition_is_met()
        {
            int delayCalls = 0;

            bool completed = await AsyncReceiveWaiter.WaitUntilAsync(
                () => delayCalls >= 3,
                TimeSpan.FromSeconds(1),
                _ =>
                {
                    delayCalls++;
                    return Task.CompletedTask;
                });

            Assert.IsTrue(completed);
            Assert.AreEqual(3, delayCalls);
        }

        [Test]
        public async Task ReceiveWaiter_times_out_without_blocking_the_thread()
        {
            int delayCalls = 0;

            bool completed = await AsyncReceiveWaiter.WaitUntilAsync(
                () => false,
                TimeSpan.FromMilliseconds(1),
                _ =>
                {
                    delayCalls++;
                    return Task.Delay(1);
                });

            Assert.IsFalse(completed);
            Assert.GreaterOrEqual(delayCalls, 1);
        }

        [Test]
        public void StopwatchPerfClock_measures_elapsed_time()
        {
            var clock = new StopwatchPerfClock();
            var sw = clock.Start();
            System.Threading.Thread.Sleep(10);
            Assert.GreaterOrEqual(sw.Elapsed.TotalMilliseconds, 5.0d);
            sw.Stop();
        }

        [Test]
        public void StopwatchWrapper_Stop_does_not_throw()
        {
            var clock = new StopwatchPerfClock();
            var sw = clock.Start();
            sw.Stop();
            sw.Stop(); // 2 回呼んでも例外なし
        }
    }
}
