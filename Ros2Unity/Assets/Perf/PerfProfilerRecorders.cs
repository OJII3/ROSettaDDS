using System;
using System.Collections.Generic;
using Unity.Profiling;

namespace ROSettaDDS.UnityPerfHarness
{
    internal sealed class PerfProfilerRecorders : IDisposable
    {
        private readonly List<RecorderEntry> _recorders = new List<RecorderEntry>();

        private PerfProfilerRecorders()
        {
        }

        internal static PerfProfilerRecorders Start()
        {
            var result = new PerfProfilerRecorders();
            result.Add("main_thread_time_ns", ProfilerCategory.Internal, "Main Thread");
            result.Add("gc_reserved_memory_bytes", ProfilerCategory.Memory, "GC Reserved Memory");
            result.Add("gc_used_memory_bytes", ProfilerCategory.Memory, "GC Used Memory");
            result.Add("system_used_memory_bytes", ProfilerCategory.Memory, "System Used Memory");
            return result;
        }

        internal Dictionary<string, object> Snapshot()
        {
            var result = new Dictionary<string, object>();
            for (int i = 0; i < _recorders.Count; i++)
            {
                RecorderEntry entry = _recorders[i];
                result[entry.Name + "_available"] = entry.Recorder.Valid;
                if (entry.Recorder.Valid)
                {
                    result[entry.Name + "_last"] = entry.Recorder.LastValue;
                }
            }
            return result;
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
            var recorder = ProfilerRecorder.StartNew(category, counterName, 128);
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
