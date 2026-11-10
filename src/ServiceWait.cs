using System;
using System.Collections.Generic;

namespace CivicPressure
{
    // Session-only observations, updated on the existing census cadence.
    internal sealed class ServiceWait
    {
        private struct Entry { internal uint Identity; internal long Since; }
        private readonly Dictionary<uint, Entry> entries = new Dictionary<uint, Entry>();
        internal bool Observe(uint id, uint identity, bool waiting, long now)
        {
            if (!waiting) { entries.Remove(id); return false; }
            Entry entry;
            if (!entries.TryGetValue(id, out entry) || entry.Identity != identity || now < entry.Since)
            { entry = new Entry { Identity = identity, Since = now }; entries[id] = entry; }
            return now - entry.Since >= GameClock.Hours(3);
        }
        internal void Forget(uint id) { entries.Remove(id); }
        internal void Clear() { entries.Clear(); }
        internal void Rebase(long now, double factor)
        {
            uint[] ids = new uint[entries.Count]; entries.Keys.CopyTo(ids, 0);
            foreach (uint id in ids)
            {
                Entry entry = entries[id]; entry.Since = GameClock.Rebase(entry.Since, now, factor);
                entries[id] = entry;
            }
        }
    }
}
