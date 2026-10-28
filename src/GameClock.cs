using System;
using System.Threading;
using System.IO;

namespace CivicPressure
{
    // Length of one "game hour" for complaints, protests and records.
    // Without Real Time the calendar runs about 112 times faster than the day/night cycle (one day in ~10 seconds),
    // so every deadline expired before players could read it. The mod therefore measures hours by the day/night
    // cycle: scale = calendar time per cycle day. With Real Time the calendar already follows the cycle, the scale is
    // 1 and nothing changes. Saved times stay calendar ticks, so dates shown in records still match the game date.
    internal static class GameClock
    {
        internal const double MaxScale = 256;
        private static long hour = TimeSpan.TicksPerHour;
        internal const string Key = "CivicPressure.Clock";
        private static byte[] saved;
        private static long savedHour;
        private static bool initialized;
        internal static bool Available = true;

        internal static void Load(byte[] data)
        {
            Reset(); saved = data;
            if (data == null || data.Length == 0) return;
            try
            {
                using (BinaryReader r = new BinaryReader(new MemoryStream(data, false)))
                {
                    if (data.Length != 16 || r.ReadInt32() != 0x4350434B || r.ReadInt32() != 1) throw new InvalidDataException("Unknown clock save");
                    savedHour = r.ReadInt64();
                    if (savedHour < TimeSpan.TicksPerHour || savedHour > TimeSpan.TicksPerHour * MaxScale) throw new InvalidDataException("Invalid clock scale");
                }
            }
            catch { Available = false; }
        }

        internal static byte[] Save()
        {
            if (!Available || !initialized) return saved;
            using (MemoryStream s = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(s))
            { w.Write(0x4350434B); w.Write(1); w.Write(HourTicks); return s.ToArray(); }
        }

        // Called before any timer is evaluated. Legacy saves have no scale: adopt the current clock once.
        internal static double Synchronize(long timePerFrame, uint framesPerDay)
        {
            long previous = initialized ? HourTicks : savedHour;
            Update(timePerFrame, framesPerDay); initialized = true;
            return previous == 0 ? 1 : HourTicks / (double)previous;
        }

        internal static long Rebase(long time, long now, double factor)
        {
            if (time == 0 || factor == 1) return time;
            decimal value = now + (time - now) * (decimal)factor;
            return (long)Math.Max(1m, Math.Min(DateTime.MaxValue.Ticks, value));
        }

        internal static long HourTicks { get { return Interlocked.Read(ref hour); } }
        internal static long MinuteTicks { get { return HourTicks / 60; } }
        internal static long DayTicks { get { return HourTicks * 24; } }
        internal static double Scale { get { return HourTicks / (double)TimeSpan.TicksPerHour; } }

        internal static long Hours(double hours) { return (long)(hours * HourTicks); }
        internal static long Minutes(double minutes) { return (long)(minutes * MinuteTicks); }

        // timePerFrame: SimulationManager.m_timePerFrame ticks; framesPerDay: SimulationManager.DAYTIME_FRAMES.
        internal static double ScaleFor(long timePerFrame, uint framesPerDay)
        {
            if (timePerFrame <= 0 || framesPerDay == 0) return 1;
            double scale = timePerFrame * (double)framesPerDay / TimeSpan.TicksPerDay;
            // Real Time rounds its frame length down, which leaves the scale a hair under 1.
            if (double.IsNaN(scale) || scale < 1.05) return 1;
            return scale > MaxScale ? MaxScale : scale;
        }

        internal static void Update(long timePerFrame, uint framesPerDay)
        {
            Interlocked.Exchange(ref hour, (long)(TimeSpan.TicksPerHour * ScaleFor(timePerFrame, framesPerDay)));
        }

        internal static void Reset() { Interlocked.Exchange(ref hour, TimeSpan.TicksPerHour); saved = null; savedHour = 0; initialized = false; Available = true; }
    }
}
