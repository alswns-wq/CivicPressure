using System;
using System.Collections.Generic;
using System.IO;

namespace CivicPressure
{
    // "Good administration pays off" (2026-09-18 user request): trust effects, residents' suggestions, NIMBY opposition to
    // unwanted facilities, and a review every 7 game days. Pure rules and save format: no game or Unity calls.
    // Highway: a loud new road next to homes (v4). Not a building, so Opposition.Road marks it and Building holds the segment.
    internal enum FacilityKind { None = 0, BusDepot = 1, Garbage = 2, PowerPlant = 3, Sewage = 4, Prison = 5, Airport = 6, Siren = 7, Heliport = 8, Cargo = 9, Highway = 10, Railway = 11 }

    // What happens when a player keeps a facility despite opposition. Chosen by difficulty (user decision).
    internal enum Enforcement { Penalty = 0, Protest = 1, Riot = 2 }

    // Agreed: conditional agreement in progress (save v3). Kept: its condition was met.
    internal enum OppositionState { Open = 0, Compensated = 1, Relocated = 2, Enforced = 3, Agreed = 4, Kept = 5 }

    // What residents ask for in a conditional agreement (user choice 2026-09-18: parks, tree belts, health measures).
    // Station: a train station near the line, connected to a transport line (v5, for railway opposition).
    internal enum AgreementCondition { None = 0, Park = 1, Trees = 2, Health = 3, Station = 4 }

    internal sealed class Opposition
    {
        internal ushort Building;
        internal uint BuildIndex;
        internal int Kind, Region, Homes, State;
        internal long Started, Deadline, NoticeDue, Closed;
        internal float X, Z;
        internal bool Riot;
        // Airports DLC: the airport area (park) this opposition is about; 0 for a single building.
        internal byte Park;
        // v4: this opposition is about a road segment (Building = segment id, BuildIndex = its build index).
        internal bool Road;
        // Conditional agreement (v3): what is asked, the count near the facility when agreed, and how many must be added.
        internal int Condition, Baseline, Needed;
        // Latest count near the facility; not saved, refreshed by the opposition scan.
        internal int Progress = -1;
    }

    internal sealed class Suggestion
    {
        internal int Region, Reason;
        internal long Started, Deadline;
        internal double Baseline;
        internal bool Accepted;
    }

    // Counters for the review period in progress.
    internal sealed class ReviewPeriod
    {
        internal long Started;
        internal int Opened, Resolved, Missed, Protests, Riots, Enforced, SuggestionsKept, SuggestionsBroken;
        internal double ScoreSum, TrustSum;
        internal int Samples;
    }

    internal sealed class Review
    {
        internal long Ended;
        internal double Score, Happiness, Handling, Trust, Calm;
        internal int Opened, Resolved, Missed, Protests, Riots, Enforced;
        internal string Grade = "";
    }

    internal static class StandingRules
    {
        // ---- Trust effects (user choices: fewer protests, more response time, happiness bonus) ----
        // Trust 0 → x1.5 protest chance, 50 → x1.0, 100 → x0.5.
        internal static double ProtestFactor(int trust) { return 1.5 - Clamp(trust) / 100.0; }
        // Above 50 trust the response time grows, up to +20% at 100.
        internal static double DeadlineFactor(int trust) { return trust <= 50 ? 1 : 1 + (Clamp(trust) - 50) / 250.0; }
        // Above 60 trust, up to +4 happiness points at 100.
        internal static double HappinessBonus(int trust) { return trust <= 60 ? 0 : (Clamp(trust) - 60) / 10.0; }
        private static int Clamp(int trust) { return trust < 0 ? 0 : trust > 100 ? 100 : trust; }

        // ---- Review every 7 game days ----
        internal const double ReviewDays = 7;
        internal const int MaxReviews = 12;

        internal static bool ReviewDue(ReviewPeriod p, long now) { return p.Started > 0 && now - p.Started >= GameClock.Hours(ReviewDays * 24); }

        internal static Review Evaluate(ReviewPeriod p, long now)
        {
            Review r = new Review { Ended = now, Opened = p.Opened, Resolved = p.Resolved, Missed = p.Missed, Protests = p.Protests, Riots = p.Riots, Enforced = p.Enforced };
            r.Happiness = p.Samples > 0 ? Bound(p.ScoreSum / p.Samples) : 50;
            int handled = p.Resolved + p.Missed;
            r.Handling = handled == 0 ? 100 : 100.0 * p.Resolved / handled;
            r.Trust = p.Samples > 0 ? Bound(p.TrustSum / p.Samples) : 50;
            r.Calm = Bound(100 - 25 * p.Protests - 40 * p.Riots - 15 * p.Enforced + 5 * p.SuggestionsKept - 5 * p.SuggestionsBroken);
            r.Score = Math.Round(r.Happiness * .4 + r.Handling * .25 + r.Trust * .2 + r.Calm * .15, 1);
            r.Grade = Grade(r.Score);
            return r;
        }

        // One hint for the weakest part of a review (Korean source text, translated by the caller).
        internal static string Advice(Review r)
        {
            if (r == null) return "";
            double handling = r.Handling - 70, calm = r.Calm - 70, trust = r.Trust - 50, happiness = r.Happiness - 60;
            double worst = Math.Min(Math.Min(handling, calm), Math.Min(trust, happiness));
            if (worst >= 0) return "좋은 행정을 유지하고 있습니다. 지금처럼 민원 기한을 지키고 주민 건의를 들어주세요.";
            if (worst == handling) return "기한을 넘긴 민원이 많습니다. 대응 기한 안에 원인을 해결해 보세요.";
            if (worst == calm) return "시위·폭동이나 주민 반대를 무시한 강행이 점수를 깎았습니다.";
            if (worst == trust) return "구청 신뢰가 낮습니다. 약속한 민원과 주민 건의를 지켜 신뢰를 회복하세요.";
            return "행복도가 낮습니다. 감점이 큰 생활 문제부터 개선해 보세요.";
        }

        internal static double HoursUntilReview(ReviewPeriod p, long now)
        {
            if (p.Started <= 0) return ReviewDays * 24;
            return Math.Max(0, (p.Started + GameClock.Hours(ReviewDays * 24) - now) / (double)GameClock.HourTicks);
        }

        internal static string Grade(double score) { return score >= 90 ? "S" : score >= 75 ? "A" : score >= 60 ? "B" : score >= 45 ? "C" : "D"; }
        private static double Bound(double value) { return double.IsNaN(value) ? 50 : value < 0 ? 0 : value > 100 ? 100 : value; }

        // ---- NIMBY opposition ----
        internal const float OppositionRadius = 400f;
        internal const int OppositionHomes = 8;
        internal const double OppositionHours = 24;
        internal const int EnforceTrust = -6, CompensateTrust = 1, RelocateTrust = 2;
        internal const double PenaltyPoints = 5, PenaltyDays = 7;

        // Conditional agreement: no money, but the residents' condition must be met within 72 game hours.
        internal const double AgreementHours = 72;
        internal const int AgreementTrust = 3;
        internal const float HealthRadius = 800f;
        // A new train station counts when it is within this distance of the opposed line (user request 2026-09-18:
        // "철도는 근처에 기차역을 하나 짓고 노선이 연결되면 해결").
        internal const float StationRadius = 1500f;
        internal const int TreesNeeded = 30, AircraftTreesNeeded = 60;

        internal static AgreementCondition ConditionFor(int kind)
        {
            switch ((FacilityKind)kind)
            {
                case FacilityKind.Garbage: case FacilityKind.Sewage: case FacilityKind.Prison: return AgreementCondition.Park;
                case FacilityKind.PowerPlant: return AgreementCondition.Health;
                case FacilityKind.Railway: return AgreementCondition.Station;
                case FacilityKind.None: return AgreementCondition.None;
                default: return AgreementCondition.Trees;
            }
        }

        internal static int Needed(int kind)
        {
            AgreementCondition c = ConditionFor(kind);
            if (c != AgreementCondition.Trees) return c == AgreementCondition.None ? 0 : 1;
            // Park, health and station conditions need one; trees need a belt.
            return kind == (int)FacilityKind.Airport || kind == (int)FacilityKind.Heliport ? AircraftTreesNeeded : TreesNeeded;
        }

        // Where new parks, trees, health buildings or stations count: the facility's radius, but wider for health care and stations.
        internal static float ConditionRadius(int kind)
        {
            AgreementCondition c = ConditionFor(kind);
            if (c == AgreementCondition.Health) return Math.Max(HealthRadius, Radius(kind));
            if (c == AgreementCondition.Station) return Math.Max(StationRadius, Radius(kind));
            return Radius(kind);
        }

        // A settled stretch of road or railway: the residents got what they asked for, so nearby works stay quiet for a while.
        internal static bool Settled(int state)
        {
            return state == (int)OppositionState.Compensated || state == (int)OppositionState.Kept || state == (int)OppositionState.Relocated;
        }

        // Counted against the number near the facility when agreed, so demolishing what was already there sets it back.
        internal static int Added(Opposition o, int current) { return Math.Max(0, current - o.Baseline); }
        internal static bool AgreementMet(Opposition o, int current) { return o.Needed > 0 && current - o.Baseline >= o.Needed; }

        internal static Enforcement Enforce(int difficulty) { return difficulty <= 2 ? Enforcement.Penalty : difficulty <= 4 ? Enforcement.Protest : Enforcement.Riot; }

        // Cost of a residents' briefing and compensation, in whole currency units.
        internal static int CompensationCost(int homes) { return Math.Min(50000, 2000 + Math.Max(0, homes) * 300); }

        internal static bool Opposes(int homes) { return homes >= OppositionHomes; }

        // Siren facilities (fire and police stations, hospitals): difficulty 5 only, a closer radius, half the cost, and no protest
        // or riot when forced through (user decision 2026-09-18: "사이렌 시설 다 같이 묶자", reaching the next houses but one).
        internal const float SirenRadius = 250f;
        internal const int SirenHomes = 6;
        // Loud new roads (highways and the like). RoadBaseAI.m_noiseAccumulation is 10 on an ordinary road and much higher
        // on highways, so it decides what counts (user request 2026-09-18: "0.6.1 님비에 고속도로도 넣어줘").
        internal const float RoadRadius = 250f;
        internal const int RoadHomes = 10;
        internal const int LoudRoadNoise = 30;
        // One opposition per stretch: a new segment this close to an opposed one is part of the same road works.
        internal const float RoadGroupRadius = 400f;
        internal static bool LoudRoad(int noiseAccumulation) { return noiseAccumulation >= LoudRoadNoise; }
        // Railways are loud whatever the asset says: a passing train is not a quiet street (user report 2026-09-18:
        // "고속도로는 되거든, 철길은 안 돼" — vanilla tracks sit below the road threshold).
        internal static bool LoudRail(int noiseAccumulation) { return noiseAccumulation > 0; }
        internal static bool Included(int kind, int difficulty) { return kind != (int)FacilityKind.Siren || difficulty >= 5; }
        // Airports and heliports are heard far away: twice the usual radius (user request 2026-09-18).
        internal const float AircraftRadius = 800f;
        // Airports DLC areas are measured from their outer edge (user request 2026-09-18). The game stores parks on a
        // 512 x 512 grid of 19.2 m cells centred on the map (DistrictManager.m_parkGrid).
        internal const int ParkGridResolution = 512;
        internal const float ParkGridCell = 19.2f;
        internal static int ParkCell(float coordinate)
        {
            int cell = (int)Math.Floor(coordinate / ParkGridCell + ParkGridResolution / 2);
            return cell < 0 ? 0 : cell >= ParkGridResolution ? ParkGridResolution - 1 : cell;
        }

        // Every cell within radius (metres) of the area, grown from its edge cells; the area itself is included.
        internal static bool[] Surroundings(bool[] area, int resolution, float cell, float radius)
        {
            bool[] result = (bool[])area.Clone();
            int r = (int)Math.Ceiling(radius / cell), r2 = r * r;
            for (int z = 0; z < resolution; z++)
                for (int x = 0; x < resolution; x++)
                {
                    int i = z * resolution + x;
                    if (!area[i]) continue;
                    bool edge = x == 0 || z == 0 || x == resolution - 1 || z == resolution - 1 || !area[i - 1] || !area[i + 1] || !area[i - resolution] || !area[i + resolution];
                    if (!edge) continue;
                    for (int dz = -r; dz <= r; dz++)
                    {
                        int zz = z + dz;
                        if (zz < 0 || zz >= resolution) continue;
                        for (int dx = -r; dx <= r; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= resolution || dx * dx + dz * dz > r2) continue;
                            result[zz * resolution + xx] = true;
                        }
                    }
                }
            return result;
        }

        internal static float Radius(int kind)
        {
            if (kind == (int)FacilityKind.Siren) return SirenRadius;
            if (kind == (int)FacilityKind.Highway || kind == (int)FacilityKind.Railway) return RoadRadius;
            return kind == (int)FacilityKind.Airport || kind == (int)FacilityKind.Heliport ? AircraftRadius : OppositionRadius;
        }
        internal static bool Opposes(int kind, int homes) { return homes >= (kind == (int)FacilityKind.Siren ? SirenHomes : Net(kind) ? RoadHomes : OppositionHomes); }
        // Roads and railways: nets, not buildings.
        internal static bool Net(int kind) { return kind == (int)FacilityKind.Highway || kind == (int)FacilityKind.Railway; }
        internal static int CompensationCost(int kind, int homes) { int cost = CompensationCost(homes); return kind == (int)FacilityKind.Siren ? cost / 2 : cost; }
        internal static Enforcement Enforce(int kind, int difficulty) { return kind == (int)FacilityKind.Siren ? Enforcement.Penalty : Enforce(difficulty); }

        // Happiness points taken from a district while an enforced facility's penalty lasts (difficulty 1-2 only).
        internal static double Penalty(Opposition o, long now)
        {
            if (o.State != (int)OppositionState.Enforced || o.Closed <= 0) return 0;
            return now - o.Closed < GameClock.Hours(PenaltyDays * 24) ? PenaltyPoints : 0;
        }

        // ---- Residents' suggestions (positive requests below the complaint threshold) ----
        // Education, healthcare, police, fire, transit access, park access.
        internal static readonly int[] SuggestionReasons = { 10, 11, 12, 14, 15, 16 };
        internal const double SuggestionHours = 72, SuggestionCooldownHours = 48;
        internal const int SuggestionKeptTrust = 3, SuggestionBrokenTrust = -1, MaxSuggestions = 4;

        internal static bool Suggests(int reason) { return Array.IndexOf(SuggestionReasons, reason) >= 0; }
        // Fulfilled when the affected share has at least halved (or vanished).
        internal static bool Fulfilled(double baseline, double current) { return current <= 0 || current <= baseline * .5; }
    }

    // Save key CivicPressure.Standing, added in 0.6.1. Older versions ignore the key.
    // v1: first DEV builds (no airport area). v2: Opposition.Park. v3: conditional agreement (Condition, Baseline, Needed; states 4-5).
    // v4: Opposition.Road (loud new roads). v5: railway opposition (kind 11, condition 4 = a connected station).
    // Keep reading every version.
    // Lesson (2026-09-18): a DEV save from v1 was unreadable after Park was added without a version bump, which switched the
    // whole feature off. Any layout change needs a new version number, even before release.
    internal static class StandingSave
    {
        internal const string Key = "CivicPressure.Standing";
        private const int Magic = 0x43505354;
        internal const int Version = 5;

        internal static byte[] Encode(ReviewPeriod period, List<Review> reviews, List<Opposition> oppositions, List<Suggestion> suggestions)
        {
            return Encode(period, reviews, oppositions, suggestions, Version);
        }

        // version: tests write older layouts to prove they still load.
        internal static byte[] Encode(ReviewPeriod period, List<Review> reviews, List<Opposition> oppositions, List<Suggestion> suggestions, int version)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(stream))
            {
                w.Write(Magic); w.Write(version);
                w.Write(period.Started); w.Write(period.Opened); w.Write(period.Resolved); w.Write(period.Missed); w.Write(period.Protests); w.Write(period.Riots);
                w.Write(period.Enforced); w.Write(period.SuggestionsKept); w.Write(period.SuggestionsBroken); w.Write(period.ScoreSum); w.Write(period.TrustSum); w.Write(period.Samples);
                w.Write(reviews.Count);
                foreach (Review r in reviews)
                {
                    w.Write(r.Ended); w.Write(r.Score); w.Write(r.Happiness); w.Write(r.Handling); w.Write(r.Trust); w.Write(r.Calm);
                    w.Write(r.Opened); w.Write(r.Resolved); w.Write(r.Missed); w.Write(r.Protests); w.Write(r.Riots); w.Write(r.Enforced); w.Write(r.Grade ?? "");
                }
                w.Write(oppositions.Count);
                foreach (Opposition o in oppositions)
                {
                    w.Write(o.Building); w.Write(o.BuildIndex); w.Write(o.Kind); w.Write(o.Region); w.Write(o.Homes); w.Write(o.State);
                    w.Write(o.Started); w.Write(o.Deadline); w.Write(o.NoticeDue); w.Write(o.Closed); w.Write(o.X); w.Write(o.Z); w.Write(o.Riot); if (version >= 2) w.Write(o.Park);
                    if (version >= 3) { w.Write((byte)o.Condition); w.Write(o.Baseline); w.Write(o.Needed); }
                    if (version >= 4) w.Write(o.Road);
                }
                w.Write(suggestions.Count);
                foreach (Suggestion s in suggestions)
                {
                    w.Write(s.Region); w.Write(s.Reason); w.Write(s.Started); w.Write(s.Deadline); w.Write(s.Baseline); w.Write(s.Accepted);
                }
                return stream.ToArray();
            }
        }

        // Throws InvalidDataException (or EndOfStreamException) on anything unexpected; the caller keeps the original bytes.
        internal static void Decode(byte[] data, out ReviewPeriod period, out List<Review> reviews, out List<Opposition> oppositions, out List<Suggestion> suggestions)
        {
            period = new ReviewPeriod(); reviews = new List<Review>(); oppositions = new List<Opposition>(); suggestions = new List<Suggestion>();
            if (data == null || data.Length == 0) return;
            if (data.Length > 200000) throw new InvalidDataException("Oversized standing data");
            using (BinaryReader r = new BinaryReader(new MemoryStream(data, false)))
            {
                if (r.ReadInt32() != Magic) throw new InvalidDataException("Unknown standing data");
                int version = r.ReadInt32();
                if (version < 1 || version > Version) throw new InvalidDataException("Unknown standing version");
                period.Started = Time(r.ReadInt64()); period.Opened = Count(r.ReadInt32()); period.Resolved = Count(r.ReadInt32()); period.Missed = Count(r.ReadInt32());
                period.Protests = Count(r.ReadInt32()); period.Riots = Count(r.ReadInt32()); period.Enforced = Count(r.ReadInt32());
                period.SuggestionsKept = Count(r.ReadInt32()); period.SuggestionsBroken = Count(r.ReadInt32());
                period.ScoreSum = Finite(r.ReadDouble()); period.TrustSum = Finite(r.ReadDouble()); period.Samples = Count(r.ReadInt32());
                int n = r.ReadInt32();
                if (n < 0 || n > StandingRules.MaxReviews) throw new InvalidDataException("Invalid review count");
                for (int i = 0; i < n; i++)
                {
                    Review v = new Review { Ended = Time(r.ReadInt64()), Score = Finite(r.ReadDouble()), Happiness = Finite(r.ReadDouble()), Handling = Finite(r.ReadDouble()),
                        Trust = Finite(r.ReadDouble()), Calm = Finite(r.ReadDouble()), Opened = Count(r.ReadInt32()), Resolved = Count(r.ReadInt32()), Missed = Count(r.ReadInt32()),
                        Protests = Count(r.ReadInt32()), Riots = Count(r.ReadInt32()), Enforced = Count(r.ReadInt32()), Grade = r.ReadString() };
                    if (v.Grade.Length > 2) throw new InvalidDataException("Invalid grade");
                    reviews.Add(v);
                }
                n = r.ReadInt32();
                if (n < 0 || n > 64) throw new InvalidDataException("Invalid opposition count");
                for (int i = 0; i < n; i++)
                {
                    Opposition o = new Opposition { Building = r.ReadUInt16(), BuildIndex = r.ReadUInt32(), Kind = r.ReadInt32(), Region = r.ReadInt32(), Homes = Count(r.ReadInt32()),
                        State = r.ReadInt32(), Started = Time(r.ReadInt64()), Deadline = Time(r.ReadInt64()), NoticeDue = Time(r.ReadInt64()), Closed = Time(r.ReadInt64()),
                        X = r.ReadSingle(), Z = r.ReadSingle(), Riot = r.ReadBoolean(), Park = version >= 2 ? r.ReadByte() : (byte)0 };
                    if (version >= 3) { o.Condition = r.ReadByte(); o.Baseline = Count(r.ReadInt32()); o.Needed = Count(r.ReadInt32()); }
                    if (version >= 4) o.Road = r.ReadBoolean();
                    if (o.Kind <= 0 || o.Kind > (version >= 5 ? 11 : version >= 4 ? 10 : 9) || o.Region < 0 || o.Region > 255 || o.State < 0 || o.State > (version >= 3 ? 5 : 3) || o.Condition > (version >= 5 ? 4 : 3) || float.IsNaN(o.X) || float.IsNaN(o.Z))
                        throw new InvalidDataException("Invalid opposition");
                    oppositions.Add(o);
                }
                n = r.ReadInt32();
                if (n < 0 || n > 64) throw new InvalidDataException("Invalid suggestion count");
                for (int i = 0; i < n; i++)
                {
                    Suggestion s = new Suggestion { Region = r.ReadInt32(), Reason = r.ReadInt32(), Started = Time(r.ReadInt64()), Deadline = Time(r.ReadInt64()),
                        Baseline = Finite(r.ReadDouble()), Accepted = r.ReadBoolean() };
                    if (s.Region < 0 || s.Region > 255 || !StandingRules.Suggests(s.Reason)) throw new InvalidDataException("Invalid suggestion");
                    suggestions.Add(s);
                }
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing standing data");
            }
        }

        private static long Time(long ticks) { if (ticks < 0 || ticks > DateTime.MaxValue.Ticks) throw new InvalidDataException("Invalid time"); return ticks; }
        private static int Count(int value) { if (value < 0) throw new InvalidDataException("Invalid count"); return value; }
        private static double Finite(double value) { if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidDataException("Invalid number"); return value; }
    }
}
