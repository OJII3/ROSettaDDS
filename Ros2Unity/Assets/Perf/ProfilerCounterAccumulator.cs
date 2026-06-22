using System.Collections.Generic;

namespace ROSettaDDS.UnityPerfHarness
{
    internal sealed class ProfilerCounterAccumulator
    {
        internal long Total { get; private set; }
        internal int Samples { get; private set; }
        internal long LastValue { get; private set; }

        internal void Add(IReadOnlyList<long> values)
        {
            for (int i = 0; i < values.Count; i++)
            {
                Add(values[i]);
            }
        }

        internal void Add(long value)
        {
            Total += value;
            LastValue = value;
            Samples++;
        }
    }
}
