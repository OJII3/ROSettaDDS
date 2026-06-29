using System;
using System.Collections.Generic;
using Unity.Profiling;

namespace ROSettaDDS.UnityPerfHarness
{
    internal sealed class PerfProfilerRecorders : IPerfProfilerSampler
    {
        private readonly List<RecorderEntry> _recorders = new List<RecorderEntry>();
        private readonly ProfilerCounterAccumulator _gcAllocatedAccumulator = new ProfilerCounterAccumulator();

        private PerfProfilerRecorders()
        {
        }

        /// <summary>
        /// lean モード: main thread / gc_used / gc_allocated の 3 recorder のみ。
        /// 計測オーバーヘッドを最小化したい perf 計測向け。
        /// 旧 06-22 baseline と同一の構成。
        /// </summary>
        internal static PerfProfilerRecorders StartLean()
        {
            var result = new PerfProfilerRecorders();
            result.Add("main_thread_time_ns", ProfilerCategory.Internal, "Main Thread");
            result.Add("gc_used_memory_bytes", ProfilerCategory.Memory, "GC Used Memory");
            result.Add("gc_allocated_in_frame_bytes", ProfilerCategory.Memory, "GC Allocated In Frame");
            return result;
        }

        /// <summary>
        /// full モード: 6 recorder (lean + メモリ詳細 3 件)。
        /// 診断用。計測オーバーヘッドが per-frame +1〜9× main thread を食うため
        /// 純粋なスループット計測には不向き。
        /// </summary>
        internal static PerfProfilerRecorders StartFull()
        {
            var result = StartLean();
            result.Add("gc_reserved_memory_bytes", ProfilerCategory.Memory, "GC Reserved Memory");
            result.Add("total_used_memory_bytes", ProfilerCategory.Memory, "Total Used Memory");
            result.Add("system_used_memory_bytes", ProfilerCategory.Memory, "System Used Memory");
            return result;
        }

        internal Dictionary<string, object> Snapshot()
        {
            Collect();

            var result = new Dictionary<string, object>();
            for (int i = 0; i < _recorders.Count; i++)
            {
                RecorderEntry entry = _recorders[i];
                result[entry.Name + "_available"] = entry.Recorder.Valid;
                if (entry.Recorder.Valid)
                {
                    result[entry.Name + "_last"] = entry.Recorder.LastValue;
                    if (entry.Name == "gc_allocated_in_frame_bytes")
                    {
                        result[entry.Name + "_last"] = _gcAllocatedAccumulator.LastValue;
                        result[entry.Name + "_total"] = _gcAllocatedAccumulator.Total;
                        result[entry.Name + "_samples"] = _gcAllocatedAccumulator.Samples;
                    }
                }
            }
            return result;
        }

        internal void Collect()
        {
            for (int i = 0; i < _recorders.Count; i++)
            {
                RecorderEntry entry = _recorders[i];
                if (entry.Name != "gc_allocated_in_frame_bytes" || !entry.Recorder.Valid)
                {
                    continue;
                }

                for (int j = 0; j < entry.Recorder.Count; j++)
                {
                    _gcAllocatedAccumulator.Add(entry.Recorder.GetSample(j).Value);
                }

                entry.Recorder.Reset();
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < _recorders.Count; i++)
            {
                _recorders[i].Recorder.Dispose();
            }
        }

        private void Add(string name, ProfilerCategory category, string counterName)
        {
            var recorder = ProfilerRecorder.StartNew(category, counterName, 4096);
            _recorders.Add(new RecorderEntry(name, recorder));
        }

        private readonly struct RecorderEntry
        {
            public RecorderEntry(string name, ProfilerRecorder recorder)
            {
                Name = name;
                Recorder = recorder;
            }

            public string Name { get; }
            public ProfilerRecorder Recorder { get; }
        }
    }
}
