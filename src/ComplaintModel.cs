using System;
using System.IO;

namespace CivicPressure
{
    // Optional events players can switch off in the options (subscriber feedback 3). Saved by UnrestSettings.
    internal static class Features
    {
        internal static volatile bool OddRequests = true, OfficeDisturbances = true, Opposition = true, Suggestions = true;
        // Saved visitor-pin visibility, shared by the options and administration tools menu.
        internal static volatile bool VisitorMarkers;
    }

    // The five values a difficulty level sets. Advanced settings tune them one by one (subscriber feedback Kalamari
    // 2026-09-18); the level itself still decides the rules that switch on and off (siren facilities, riot on enforcing).
    internal enum DifficultyValue { Penalty = 0, Frequency = 1, Persistence = 2, Deadline = 3, Office = 4 }

    internal static class Difficulty
    {
        internal static volatile int Level = 3;
        private static readonly double[] penalties = { .45, .7, 1, 1.2, 1.45 }, frequencies = { .35, .65, 1, 1.5, 2.2 }, persistence = { 24, 16, 12, 8, 4 }, deadlines = { 48, 36, 24, 15, 9 }, offices = { 48, 36, 24, 16, 8 };
        private static readonly double[] minimums = { .2, 0, 1, 4, 4 }, maximums = { 2, 3, 48, 96, 96 };
        // Advanced settings: custom values replace the level's own numbers (the level's rules stay).
        internal static volatile bool Custom;
        private static readonly double[] chosen = { 1, 1, 12, 24, 24 };

        internal static double Minimum(int value) { return minimums[Bound(value)]; }
        internal static double Maximum(int value) { return maximums[Bound(value)]; }
        internal static double Clamp(int value, double amount)
        {
            int i = Bound(value);
            return double.IsNaN(amount) ? Default(i, Level) : amount < minimums[i] ? minimums[i] : amount > maximums[i] ? maximums[i] : amount;
        }

        internal static double Default(int value, int level)
        {
            int i = Bound(value), n = level < 1 || level > 5 ? 3 : level;
            switch ((DifficultyValue)i)
            {
                case DifficultyValue.Penalty: return penalties[n - 1];
                case DifficultyValue.Frequency: return frequencies[n - 1];
                case DifficultyValue.Persistence: return persistence[n - 1];
                case DifficultyValue.Deadline: return deadlines[n - 1];
                default: return offices[n - 1];
            }
        }

        internal static double Value(int value) { int i = Bound(value); return Custom ? chosen[i] : Default(i, Level); }
        internal static void Set(int value, double amount) { chosen[Bound(value)] = Clamp(value, amount); }
        private static int Bound(int value) { return value < 0 ? 0 : value > 4 ? 4 : value; }

        internal static double PenaltyScale { get { return Value((int)DifficultyValue.Penalty); } }
        internal static double Frequency { get { return Value((int)DifficultyValue.Frequency); } }
        internal static double PersistenceHours { get { return Value((int)DifficultyValue.Persistence); } }
        internal static double DeadlineHours { get { return Value((int)DifficultyValue.Deadline); } }
        internal static double OfficeHours { get { return Value((int)DifficultyValue.Office); } }
    }

    internal sealed class ComplaintRecord
    {
        internal int Region, Reason;
        internal int Variant;
        internal long Since, Deadline, QuietUntil, OfficeUntil;
        internal long ImprovingSince;
        internal bool Open, Warned, Promised;
        internal double LaborBaseline;
        internal bool LaborExtended;
    }

    internal static class ComplaintRules
    {
        internal const int ReasonCount = 26, OddReason = 26, Slots = 27;
        internal static bool Labor(int reason) { return reason == 9 || reason == 22 || reason == 23; }
        internal static double ResponseHours(int reason) { return Difficulty.DeadlineHours * (Labor(reason) ? 3 : 1); }
        internal static bool Slow(int reason) { return reason == 9 || reason == 10 || (reason >= 22 && reason <= 24); }
        internal static bool Essential(int reason) { return reason == 3 || reason == 13 || reason == 14 || reason == 18 || reason == 25; }
        internal static double OpenRatio(int reason) { return Essential(reason) ? .001 : reason == 9 ? .05 : Labor(reason) ? .03 : reason == 6 ? .02 : .01; }
        internal static double Rate(int affected, int severe, int residents) { return residents <= 0 ? 0 : Happiness.Weighted(affected, severe, residents) / residents; }
        internal static bool Recovered(int reason, int affected, int severe, int residents)
        { return Rate(affected, severe, residents) <= CloseRatio(reason); }
        internal static double CloseRatio(int reason) { return reason == 9 ? .03 : OpenRatio(reason) / 2; }
        internal static bool UpdateCounts(ComplaintRecord c, int affected, int severe, int residents, long now)
        {
            // Every real complaint uses regional statistics, including severe residual cases.
            if (affected <= 0 || residents <= 0) return Update(c, false, now);
            double fraction = Rate(affected, severe, residents);
            if (!c.Open) {
                bool changed = Update(c, fraction > OpenRatio(c.Reason), now);
                if (changed && Labor(c.Reason)) { c.LaborBaseline = fraction; c.LaborExtended = false; }
                return changed;
            }
            if (Labor(c.Reason)) {
                // Old saves get one matching period; the saved baseline prevents another grant on reload.
                if (c.LaborBaseline == 0) {
                    c.LaborBaseline = fraction;
                    c.Deadline = Math.Max(c.Deadline, now + GameClock.Hours(ResponseHours(c.Reason)));
                    if (c.OfficeUntil > 0) c.OfficeUntil = Math.Max(c.OfficeUntil, c.Deadline);
                    c.Warned = false;
                }
                // A material reduction buys time once, without treating vacancies or transit as actual employment.
                if (!c.LaborExtended && fraction > CloseRatio(c.Reason) && fraction <= c.LaborBaseline * .8) {
                    c.LaborExtended = true; c.Warned = false;
                    c.Deadline = Math.Max(now, c.Deadline) + GameClock.Hours(ResponseHours(c.Reason) / 2);
                    if (c.OfficeUntil > 0) c.OfficeUntil = Math.Max(c.OfficeUntil, c.Deadline);
                }
            }
            if (fraction <= CloseRatio(c.Reason))
            {
                if (c.ImprovingSince == 0 || c.ImprovingSince > now) c.ImprovingSince = now;
                if (now - c.ImprovingSince >= GameClock.HourTicks * 3) return Update(c, false, now);
            }
            else c.ImprovingSince = 0;
            return false;
        }
        private static readonly string[] OddRequestsSource = {
            "제가 나올 때까지 집 앞 버스를 기다리게 해 주세요.",
            "옆 동네보다 우리 동네 이름을 지도에 더 크게 써 주세요.",
            "야경도 보고 싶고 밝았으면 좋겠어요. 밤에도 해를 띄워 주세요.",
            "신호등이 제 차만 알아보고 초록불로 바뀌게 해 주세요.",
            "월요일을 도시 조례로 폐지해 주세요.",
            "구청 앞 비둘기에게도 명예 시민증을 발급해 주세요."
        };
        internal static string[] OddRequests { get { return L.Array(OddRequestsSource); } }
        private static readonly string[] OddAnswersSource = {
            "버스는 모두의 시간표로 운행합니다. 개인 대기 요청은 반영하기 어렵습니다.",
            "지도 글자 크기는 동네 자존심 순위가 아닙니다. 기존 표기를 유지합니다.",
            "태양의 야간 근무는 행정청 권한 밖입니다. 양해 부탁드립니다.",
            "신호등은 모든 시민에게 공평하게 운영하겠습니다.",
            "월요일 폐지 시 화요일이 같은 민원을 제기할 것으로 검토됐습니다.",
            "비둘기 본인의 서명을 받지 못했습니다. 시민증 발급은 보류합니다."
        };
        internal static string[] OddAnswers { get { return L.Array(OddAnswersSource); } }
        internal static bool Escalates(ComplaintRecord c, long now)
        { return c.Reason != OddReason && c.Open && c.ImprovingSince == 0 && now >= c.QuietUntil && now >= c.Deadline; }
        internal static void LimitOddCooldown(ComplaintRecord c, long now)
        {
            if (c.Reason == OddReason && !c.Open)
                c.QuietUntil = Math.Min(c.QuietUntil, now + GameClock.HourTicks * 2);
        }
        internal static bool FinishOdd(ComplaintRecord c, long now, bool regionExists)
        {
            if (c.Reason != OddReason || !c.Open || (regionExists && now < c.Deadline)) return false;
            c.Open = false; c.QuietUntil = now + GameClock.HourTicks * 2;
            return true;
        }
        internal static bool Decide(ComplaintRecord c, long stamp, bool accept, long now)
        {
            if (!c.Open || c.Since != stamp || c.Promised) return false;
            if (accept)
            {
                if (now >= c.Deadline) return false;
                c.Promised = true; c.Warned = false;
                c.Deadline = c.Reason == OddReason ? now + GameClock.HourTicks * 6 : c.Deadline + GameClock.Hours(ResponseHours(c.Reason) / 2);
            }
            else
            {
                c.Open = false; c.Since = 0; c.Deadline = 0; c.OfficeUntil = 0; c.Warned = false; c.ImprovingSince = 0;
                c.QuietUntil = now + GameClock.HourTicks * (c.Reason == OddReason ? 2 : 24);
            }
            return true;
        }
        private static readonly double[] UtilityHours = { 3, 2, 1, .5, 0 };
        // Level 5 opens at the first completed census reporting an outage; no extra timer.
        internal static double IntakeHours(int reason) { return reason == 3 || reason == 13 || reason == 14 || reason == 18 || reason == 25 ? UtilityHours[Difficulty.Level - 1] : Difficulty.PersistenceHours * (Labor(reason) ? 4 : Slow(reason) ? 2 : 1); }
        internal static double RoadPriority(int affected, int severe, int population)
        {
            if (affected <= 0 || population <= 0) return 0;
            return (affected + .5 * Math.Min(affected, severe)) * Math.Sqrt(Math.Min(1, affected / (double)population));
        }
        internal static bool Update(ComplaintRecord c, bool problem, long now)
        {
            if (!problem)
            {
                bool closed = c.Open;
                c.Open = false; c.Since = 0; c.Deadline = 0; c.OfficeUntil = 0; c.Warned = false; c.Promised = false; c.ImprovingSince = 0;
                c.LaborBaseline = 0; c.LaborExtended = false;
                if (closed) c.QuietUntil = now + GameClock.HourTicks * 24;
                return closed;
            }
            if (now < c.QuietUntil) return false;
            if (c.Since == 0) c.Since = now;
            if (!c.Open && now - c.Since >= GameClock.Hours(IntakeHours(c.Reason)))
            {
                c.Open = true; c.Deadline = now + GameClock.Hours(ResponseHours(c.Reason));
                return true;
            }
            return false;
        }
    }

    internal static class ComplaintSave
    {
        internal const string Key = "CivicPressure.Complaints";
        internal static byte[] Encode(ComplaintRecord[] records)
        {
            using (MemoryStream s = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(s))
            {
                w.Write(0x43504331); w.Write(8); w.Write(records.Length);
                foreach (ComplaintRecord c in records)
                {
                    w.Write(c.Region); w.Write(c.Reason); w.Write(c.Since); w.Write(c.Deadline);
                    w.Write(c.QuietUntil); w.Write(c.OfficeUntil); w.Write(c.Open); w.Write(c.Warned); w.Write(c.Promised);
                    w.Write(c.Variant);
                    w.Write(c.ImprovingSince);
                    w.Write(c.LaborBaseline); w.Write(c.LaborExtended);
                }
                return s.ToArray();
            }
        }
        internal static ComplaintRecord[] Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return new ComplaintRecord[0];
            if (bytes.Length > 500000) throw new InvalidDataException("Complaint save too large");
            using (BinaryReader r = new BinaryReader(new MemoryStream(bytes, false)))
            {
                if (r.ReadInt32() != 0x43504331) throw new InvalidDataException("Unknown complaint save");
                int version = r.ReadInt32();
                if (version < 1 || version > 8) throw new InvalidDataException("Unknown complaint save");
                int slots = version == 1 ? 13 : version == 2 ? 15 : version == 3 ? 16 : version == 4 ? 18 : version == 5 ? 19 : ComplaintRules.Slots;
                int count = r.ReadInt32();
                if (count < 0 || count > 256 * slots || bytes.Length != 12 + count * (version == 1 ? 43 : version == 2 ? 47 : version < 8 ? 55 : 64)) throw new InvalidDataException("Invalid complaint count");
                bool[] seen = new bool[256 * slots];
                ComplaintRecord[] result = new ComplaintRecord[count];
                for (int i = 0; i < count; i++)
                {
                    ComplaintRecord c = new ComplaintRecord { Region = r.ReadInt32(), Reason = r.ReadInt32(), Since = r.ReadInt64(), Deadline = r.ReadInt64(), QuietUntil = r.ReadInt64(), OfficeUntil = r.ReadInt64(), Open = r.ReadBoolean(), Warned = r.ReadBoolean(), Promised = r.ReadBoolean() };
                    if (version >= 2) c.Variant = r.ReadInt32();
                    if (version >= 3) c.ImprovingSince = r.ReadInt64();
                    if (version >= 8) {
                        c.LaborBaseline = r.ReadDouble(); c.LaborExtended = r.ReadBoolean();
                        if (double.IsNaN(c.LaborBaseline) || c.LaborBaseline < 0 || c.LaborBaseline > 1.5 ||
                            (c.LaborExtended && c.LaborBaseline == 0)) throw new InvalidDataException("Invalid labor baseline");
                    }
                    if (c.Region < 0 || c.Region > 255 || c.Reason < 0 || c.Reason >= slots || seen[c.Region * slots + c.Reason]) throw new InvalidDataException("Invalid complaint identity");
                    seen[c.Region * slots + c.Reason] = true;
                    if ((version == 2 && c.Reason == 14) || (version == 3 && c.Reason == 15) || (version == 4 && c.Reason == 17) || (version == 5 && c.Reason == 18)) c.Reason = ComplaintRules.OddReason;
                    if (c.Variant < 0 || c.Variant >= ComplaintRules.OddRequests.Length || (c.Reason != ComplaintRules.OddReason && c.Variant != 0)) throw new InvalidDataException("Invalid request variant");
                    foreach (long time in new long[] { c.Since, c.Deadline, c.QuietUntil, c.OfficeUntil, c.ImprovingSince })
                        if (time < 0 || time > DateTime.MaxValue.Ticks) throw new InvalidDataException("Invalid complaint time");
                    if (c.ImprovingSince != 0 && (!c.Open || c.Reason == ComplaintRules.OddReason)) throw new InvalidDataException("Invalid improvement timer");
                    result[i] = c;
                }
                return result;
            }
        }
    }
}
