using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    // Rewards for good administration and NIMBY opposition (2026-09-18 user request). Game side of StandingModel.
    // Runs on the simulation thread under CivicSimulation.Gate (from CivicComplaints.Tick); saved under StandingSave.Key.
    internal sealed class StandingView
    {
        internal bool Available;
        internal Review[] Reviews = new Review[0];
        internal Review Current;
        internal double HoursLeft;
        internal int Kept, Broken;
    }

    internal static class CivicStanding
    {
        internal static void Rebase(long now, double factor)
        {
            period.Started = GameClock.Rebase(period.Started, now, factor);
            nextSample = GameClock.Rebase(nextSample, now, factor);
            nextSuggestion = GameClock.Rebase(nextSuggestion, now, factor);
            foreach (Opposition o in oppositions) {
                o.Started = GameClock.Rebase(o.Started, now, factor);
                o.Deadline = GameClock.Rebase(o.Deadline, now, factor);
                o.NoticeDue = GameClock.Rebase(o.NoticeDue, now, factor);
                if (o.Closed > 1) o.Closed = GameClock.Rebase(o.Closed, now, factor);
            }
            foreach (Suggestion s in suggestions) {
                s.Started = GameClock.Rebase(s.Started, now, factor);
                s.Deadline = GameClock.Rebase(s.Deadline, now, factor);
            }
        }
        // Complaint list keys: buildings 0-65535, airport areas 65536+, road segments 131072+ (v4).
        internal const int OppositionKeyBase = 200000, RoadKeyOffset = 131072, SuggestionKeyBase = 400000;
        private static ReviewPeriod period = new ReviewPeriod();
        private static List<Review> reviews = new List<Review>();
        private static List<Opposition> oppositions = new List<Opposition>();
        private static List<Suggestion> suggestions = new List<Suggestion>();
        private static byte[] preserved;
        private static readonly Dictionary<ushort, uint> known = new Dictionary<ushort, uint>();
        private static bool baselined;
        private static int scanTick;
        private static long nextSample, nextSuggestion;
        private static readonly System.Random random = new System.Random();
        internal static string Error { get { return preserved == null ? "" : L.T("행정 평가 저장 데이터 오류 · 원본 보존 중"); } }

        internal static void Reset()
        {
            period = new ReviewPeriod(); reviews = new List<Review>(); oppositions = new List<Opposition>(); suggestions = new List<Suggestion>();
            preserved = null; known.Clear(); knownAreas.Clear(); knownRoads.Clear(); baselined = false; roadsBaselined = false; scanTick = 0; nextSample = 0; nextSuggestion = 0;
            UnseenGrade = "";
        }

        internal static void Load(byte[] data)
        {
            Reset();
            try
            {
                StandingSave.Decode(data, out period, out reviews, out oppositions, out suggestions);
                // Railway opposition was dropped before release; a DEV city may still carry one. Read it, then let it go.
                oppositions.RemoveAll(delegate(Opposition o) { return o.Kind == (int)FacilityKind.Railway; });
            }
            catch (Exception error)
            {
                Reset(); preserved = data;
                Debug.LogWarning("[CivicPressure] Standing data kept unread: " + error.Message);
            }
        }

        internal static byte[] Save()
        {
            if (preserved != null) return preserved;
            return StandingSave.Encode(period, reviews, oppositions, suggestions);
        }

        // ---- Counters from the rest of the mod ----
        internal static void Opened() { if (preserved == null) period.Opened++; }
        internal static void Resolved() { if (preserved == null) period.Resolved++; }
        internal static void Missed() { if (preserved == null) period.Missed++; }
        internal static void Incident(bool riot) { if (preserved == null) { if (riot) period.Riots++; else period.Protests++; } }

        // ---- Trust and opposition effects on district happiness (CivicSimulation.Publish) ----
        internal static double Adjustment(int region, bool hasOffice, long now)
        {
            double value = hasOffice ? StandingRules.HappinessBonus(CivicRecords.Trust(region)) : 0;
            if (Features.Opposition) foreach (Opposition o in oppositions) if (o.Region == region) value -= StandingRules.Penalty(o, now);
            return value;
        }

        internal static Review Latest { get { return reviews.Count == 0 ? null : reviews[reviews.Count - 1]; } }

        // Grade of a review the player has not opened yet (panel button badge). Cleared by the review window.
        internal static volatile string UnseenGrade = "";

        // Immutable copy for the UI thread, taken under CivicSimulation.Gate.
        internal static StandingView Snapshot(long now)
        {
            StandingView v = new StandingView { Available = preserved == null, Reviews = reviews.ToArray(), HoursLeft = StandingRules.HoursUntilReview(period, now) };
            v.Current = StandingRules.Evaluate(period, now);
            v.Kept = period.SuggestionsKept; v.Broken = period.SuggestionsBroken;
            return v;
        }

        // Developer tools (DEV build buttons): consumed on the next tick.
        internal static volatile bool DevOppose, DevSuggest, DevReview;
        internal static volatile string DevStatus = "";

        internal static void Tick(CityView city, DateTime now)
        {
            if (preserved != null || city.All == null) return;
            long t = now.Ticks;
            if (period.Started == 0 || period.Started > t) period.Started = t;
            if (DevReview) { DevReview = false; period.Started = t - GameClock.Hours(StandingRules.ReviewDays * 24); DevStatus = "7일 평가를 바로 계산합니다."; }
            if (DevSuggest) { DevSuggest = false; nextSuggestion = 0; forceSuggestion = true; }
            if (DevOppose) { DevOppose = false; try { ForceOpposition(city, t); } catch (Exception error) { DevStatus = "시설 반대 시험 실패: " + error.Message; } }
            Sample(city, t);
            if (StandingRules.ReviewDue(period, t)) Conclude(city, t);
            try { if (++scanTick % 10 == 1) Scan(city, t); Oppose(city, t); }
            catch (Exception error) { DevStatus = "시설 반대 처리 오류: " + error.Message; Debug.LogWarning("[CivicPressure] Facility opposition skipped: " + error.Message); }
            try { Suggest(city, t); }
            catch (Exception error) { Debug.LogWarning("[CivicPressure] Suggestions skipped: " + error.Message); }
        }

        private static bool forceSuggestion;

        // DEV: opposition against the existing facility with the most homes nearby, ignoring the minimum.
        private static void ForceOpposition(CityView city, long now)
        {
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            DistrictManager districts = Singleton<DistrictManager>.instance;
            ushort best = 0; int bestHomes = -1;
            // Airports DLC areas compete with single buildings (the button used to ignore them).
            byte bestPark = 0;
            int areas = 0;
            for (int p = 1; p < 256 && p < districts.m_parks.m_buffer.Length; p++)
            {
                if ((districts.m_parks.m_buffer[p].m_flags & DistrictPark.Flags.Created) == 0 || !districts.m_parks.m_buffer[p].IsAirport) continue;
                areas++;
                bool pending = false;
                foreach (Opposition x in oppositions) if (x.Park == p && Pending(x)) pending = true;
                if (pending) continue;
                int homes = HomesNearArea(districts, buildings, (byte)p, StandingRules.Radius((int)FacilityKind.Airport));
                if (homes > bestHomes) { bestPark = (byte)p; bestHomes = homes; }
            }
            for (int i = 1; i < buildings.Length; i++)
            {
                if (!CivicSimulation.Live(buildings[i]) || Classify(buildings[i].Info) == FacilityKind.None || !StandingRules.Included((int)Classify(buildings[i].Info), Difficulty.Level)) continue;
                if (Classify(buildings[i].Info) == FacilityKind.Airport && AirportArea(districts, buildings[i].m_position) != 0) continue;
                bool tracked = false;
                foreach (Opposition x in oppositions) if (x.Building == i && x.BuildIndex == buildings[i].m_buildIndex && Pending(x)) tracked = true;
                if (tracked) continue;
                int homes = HomesNear(buildings, buildings[i].m_position, StandingRules.Radius((int)Classify(buildings[i].Info)));
                if (homes > bestHomes) { best = (ushort)i; bestPark = 0; bestHomes = homes; }
            }
            // Loud roads compete too, so the button can test road opposition.
            NetSegment[] roadSegments = Singleton<NetManager>.instance.m_segments.m_buffer;
            ushort bestRoad = 0;
            int loudRoads = 0, loudRails = 0, railNoise = 0;
            for (int i = 1; i < roadSegments.Length; i++)
            {
                if ((roadSegments[i].m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted | NetSegment.Flags.Untouchable)) != NetSegment.Flags.Created) continue;
                FacilityKind netKind = LoudNet(roadSegments[i].Info);
                if (netKind == FacilityKind.None) continue;
                if (netKind == FacilityKind.Railway)
                {
                    loudRails++;
                    TrainTrackBaseAI trackAI = roadSegments[i].Info.m_netAI as TrainTrackBaseAI;
                    if (trackAI != null && trackAI.m_noiseAccumulation > railNoise) railNoise = trackAI.m_noiseAccumulation;
                }
                else loudRoads++;
                Vector3 spot = roadSegments[i].m_middlePosition;
                if (RoadTracked(spot, new List<Vector3>(), netKind, now)) continue;
                int homes = HomesNear(buildings, spot, StandingRules.Radius((int)LoudNet(roadSegments[i].Info)));
                if (homes > bestHomes) { bestRoad = (ushort)i; best = 0; bestPark = 0; bestHomes = homes; }
            }
            if (bestRoad != 0)
            {
                Vector3 spot = roadSegments[bestRoad].m_middlePosition;
                int roadRegion = Singleton<DistrictManager>.instance.GetDistrict(spot);
                if (roadRegion >= city.Regions.Length || city.Regions[roadRegion] == null) roadRegion = 0;
                Opposition road = new Opposition { Building = bestRoad, BuildIndex = roadSegments[bestRoad].m_buildIndex, Road = true, Kind = (int)LoudNet(roadSegments[bestRoad].Info), Region = roadRegion,
                    Homes = Math.Max(bestHomes, StandingRules.RoadHomes), State = (int)OppositionState.Open, Started = now,
                    Deadline = now + GameClock.Hours(StandingRules.OppositionHours), X = spot.x, Z = spot.z };
                oppositions.Add(road);
                knownRoads[bestRoad] = roadSegments[bestRoad].m_buildIndex;
                DevStatus = "시설 반대 발생: 구간 #" + bestRoad + " (" + KindName(road.Kind) + ", 반경 250m 주택 " + bestHomes + "채 · 소음 도로 " + loudRoads + "구간, 철도 " + loudRails + "구간 소음값 " + railNoise + ")";
                CivicChirper.PostComplaint(Area(city, roadRegion) + " · " + KindName(road.Kind) + (road.Kind == (int)FacilityKind.Railway
                    ? L.T(" · 인근 주민들이 열차 소음을 걱정하며 공사에 반대 의견을 제출했습니다. 행정청이 주민 설명회와 보상 여부를 검토합니다. #시설반대")
                    : L.T(" · 인근 주민들이 도로 소음을 걱정하며 공사에 반대 의견을 제출했습니다. 행정청이 주민 설명회와 보상 여부를 검토합니다. #시설반대")), city, roadRegion);
                return;
            }
            if (bestPark != 0)
            {
                AddArea(city, districts, bestPark, Math.Max(bestHomes, StandingRules.OppositionHomes), now);
                knownAreas[bestPark] = true;
                Vector3 spot = districts.m_parks.m_buffer[bestPark].m_nameLocation;
                DevStatus = "시설 반대 발생: 공항 영역 #" + bestPark + " (경계 800m 안 주택 " + bestHomes + "채, 영역 칸 " + LastAreaCells +
                    (LastAreaCells == 0 ? " → 중심 반경으로 대체" : "") + ", 공항 영역 " + areas + "곳, 중심 " + spot.x.ToString("0") + "/" + spot.z.ToString("0") + ")";
                return;
            }
            if (best == 0) { DevStatus = "시설 반대 시험: 대상이 없습니다 (소음 도로 " + loudRoads + "구간, 철도 " + loudRails + "구간 소음값 " + railNoise + " · 이미 반대 중이거나 주변 주택이 없을 수 있습니다)."; return; }
            Building b = buildings[best];
            int region = Singleton<DistrictManager>.instance.GetDistrict(b.m_position);
            if (region >= city.Regions.Length || city.Regions[region] == null) region = 0;
            Opposition o = new Opposition { Building = best, BuildIndex = b.m_buildIndex, Kind = (int)Classify(b.Info), Region = region, Homes = Math.Max(bestHomes, StandingRules.OppositionHomes),
                State = (int)OppositionState.Open, Started = now, Deadline = now + GameClock.Hours(StandingRules.OppositionHours), X = b.m_position.x, Z = b.m_position.z };
            oppositions.Add(o);
            DevStatus = "시설 반대 발생: 건물 #" + best + " (" + KindName(o.Kind) + ", 주변 주택 " + bestHomes + "채)";
            CivicChirper.PostComplaint(Area(city, region) + " · " + KindName(o.Kind) + L.T(" · 인근 주민들이 시설 설치에 반대 의견을 제출했습니다. 행정청이 주민 설명회와 보상 여부를 검토합니다. #시설반대"), city, region);
        }

        private static void Sample(CityView city, long now)
        {
            if (now < nextSample && nextSample - now <= GameClock.HourTicks) return;
            nextSample = now + GameClock.HourTicks;
            if (double.IsNaN(city.CityScore)) return;
            int offices = 0; double trust = 0;
            for (int i = 1; i < city.Regions.Length; i++) if (city.Regions[i] != null && city.Regions[i].OfficeId != 0) { offices++; trust += CivicRecords.Trust(i); }
            period.ScoreSum += city.CityScore;
            period.TrustSum += offices == 0 ? 50 : trust / offices;
            period.Samples++;
        }

        private static void Conclude(CityView city, long now)
        {
            Review r = StandingRules.Evaluate(period, now);
            reviews.Add(r);
            while (reviews.Count > StandingRules.MaxReviews) reviews.RemoveAt(0);
            period = new ReviewPeriod { Started = now };
            // Short notice only; the details are in the review window ("평가" button in the administration panel).
            UnseenGrade = r.Grade;
            CivicChirper.PostText(L.T("지난 7일 행정 평가: ") + r.Grade + " (" + r.Score.ToString("0") + L.T("점). 자세한 내용은 도시 행정 창의 '평가'에서 확인할 수 있습니다. #행정평가"));
            Debug.Log("[CivicPressure] Review: grade=" + r.Grade + ", score=" + r.Score);
        }

        // ---- NIMBY ----
        internal static FacilityKind Classify(BuildingInfo info)
        {
            if (info == null || info.m_class == null) return FacilityKind.None;
            BuildingAI ai = info.m_buildingAI;
            ItemClass c = info.m_class;
            if (c.m_service == ItemClass.Service.Garbage) return FacilityKind.Garbage;
            if (ai is PowerPlantAI && !(ai is WindTurbineAI) && !(ai is SolarPowerPlantAI) && !(ai is DamPowerHouseAI) && !(ai is FusionPowerPlantAI)) return FacilityKind.PowerPlant;
            if (ai is WaterFacilityAI && ((WaterFacilityAI)ai).m_sewageOutlet > 0) return FacilityKind.Sewage;
            if (ai is PoliceStationAI && c.m_level >= ItemClass.Level.Level4) return FacilityKind.Prison;
            // Helicopter depots (Natural Disasters: fire, police, medical): an ordinary unwanted facility at every difficulty (user request 2026-09-18).
            if (ai is HelicopterDepotAI) return FacilityKind.Heliport;
            if (ai is FireStationAI || ai is HospitalAI || ai is PoliceStationAI) return FacilityKind.Siren;
            if (ai is DepotAI && c.m_service == ItemClass.Service.PublicTransport && c.m_subService == ItemClass.SubService.PublicTransportBus) return FacilityKind.BusDepot;
            // Passenger and cargo airports are loud (800 m); cargo train stations and harbors keep the usual radius.
            if (c.m_service == ItemClass.Service.PublicTransport && c.m_subService == ItemClass.SubService.PublicTransportPlane && (ai is TransportStationAI || ai is CargoStationAI)) return FacilityKind.Airport;
            if (ai is CargoStationAI) return FacilityKind.Cargo;
            return FacilityKind.None;
        }

        internal static string KindName(int kind)
        {
            switch ((FacilityKind)kind)
            {
                case FacilityKind.BusDepot: return L.T("버스 차고지");
                case FacilityKind.Garbage: return L.T("쓰레기 시설");
                case FacilityKind.PowerPlant: return L.T("발전소");
                case FacilityKind.Sewage: return L.T("하수 처리 시설");
                case FacilityKind.Prison: return L.T("교도소");
                case FacilityKind.Heliport: return L.T("헬기장");
                case FacilityKind.Siren: return L.T("사이렌 시설(소방서·경찰서·병원)");
                case FacilityKind.Airport: return L.T("공항");
                case FacilityKind.Highway: return L.T("소음이 큰 도로(고속도로 등)");
                case FacilityKind.Railway: return L.T("철도 선로");
                default: return L.T("화물 터미널");
            }
        }

        private static void Scan(CityView city, long now)
        {
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            DistrictManager districts = Singleton<DistrictManager>.instance;
            List<ushort> fresh = new List<ushort>();
            bool[] airportInUse = new bool[256];
            for (int i = 1; i < buildings.Length; i++)
            {
                Building b = buildings[i];
                if (!CivicSimulation.Live(b)) continue;
                byte park = AirportArea(districts, b.m_position);
                if (park != 0) airportInUse[park] = true;
                if (Classify(b.Info) == FacilityKind.None) continue;
                ushort id = (ushort)i;
                uint index;
                if (known.TryGetValue(id, out index) && index == b.m_buildIndex) continue;
                known[id] = b.m_buildIndex;
                if (baselined) fresh.Add(id);
            }
            MarkRunways(districts, airportInUse);
            // Airports DLC: one opposition per airport area, once it has a building, runway or taxiway. Areas in use at load time
            // are accepted; an empty area stays unknown, so building in it later still meets opposition (bug found 2026-09-18).
            List<byte> freshAreas = new List<byte>();
            for (int p = 1; p < 256 && p < districts.m_parks.m_buffer.Length; p++)
            {
                bool airport = (districts.m_parks.m_buffer[p].m_flags & DistrictPark.Flags.Created) != 0 && districts.m_parks.m_buffer[p].IsAirport;
                if (!airport) { knownAreas.Remove((byte)p); continue; }
                if (knownAreas.ContainsKey((byte)p) || !airportInUse[p]) continue;
                knownAreas[(byte)p] = true;
                if (baselined) freshAreas.Add((byte)p);
            }
            // Facilities already in the city when it loads are accepted; only new ones meet opposition.
            baselined = true;
            try { ScanRoads(city, buildings, now); }
            catch (Exception error) { Debug.LogWarning("[CivicPressure] Road opposition skipped: " + error.Message); }
            if (!Features.Opposition) return;
            foreach (byte park in freshAreas) OpposeArea(city, districts, buildings, park, now);
            if (fresh.Count == 0) return;
            foreach (ushort id in fresh)
            {
                Building b = buildings[id];
                bool tracked = false;
                foreach (Opposition known2 in oppositions) if (known2.Building == id && known2.BuildIndex == b.m_buildIndex) tracked = true;
                if (tracked || oppositions.Count >= 32) continue;
                int kindId = (int)Classify(b.Info);
                // Airport buildings inside an airport area are covered by the area's own opposition.
                if (kindId == (int)FacilityKind.Airport && AirportArea(districts, b.m_position) != 0) continue;
                // Siren facilities count as known at any difficulty, but only difficulty 5 opposes them.
                if (!StandingRules.Included(kindId, Difficulty.Level)) continue;
                int homes = HomesNear(buildings, b.m_position, StandingRules.Radius(kindId));
                if (!StandingRules.Opposes(kindId, homes)) continue;
                int region = Singleton<DistrictManager>.instance.GetDistrict(b.m_position);
                if (region >= city.Regions.Length || city.Regions[region] == null) region = 0;
                Opposition o = new Opposition { Building = id, BuildIndex = b.m_buildIndex, Kind = (int)Classify(b.Info), Region = region, Homes = homes,
                    State = (int)OppositionState.Open, Started = now, Deadline = now + GameClock.Hours(StandingRules.OppositionHours), X = b.m_position.x, Z = b.m_position.z };
                oppositions.Add(o);
                CivicChirper.PostComplaint(Area(city, region) + " · " + KindName(o.Kind) + (o.Kind == (int)FacilityKind.Siren
                    ? L.T(" · 인근 주민들이 사이렌 소음을 걱정하며 시설 설치에 반대 의견을 제출했습니다. 행정청이 주민 설명회와 보상 여부를 검토합니다. #시설반대")
                    : L.T(" · 인근 주민들이 시설 설치에 반대 의견을 제출했습니다. 행정청이 주민 설명회와 보상 여부를 검토합니다. #시설반대")), city, region);
            }
        }

        private static readonly Dictionary<byte, bool> knownAreas = new Dictionary<byte, bool>();
        private static readonly Dictionary<ushort, uint> knownRoads = new Dictionary<ushort, uint>();

        // Loud roads: highways and anything else whose noise is well above an ordinary street (RoadBaseAI.m_noiseAccumulation).
        // Tunnels are underground, pedestrian streets are quiet, so both are left out.
        internal static bool LoudRoad(NetInfo info) { return LoudNet(info) == FacilityKind.Highway; }

        // Which loud net this is, if any: a highway-grade road or a railway line. Tunnels are underground and quiet.
        internal static FacilityKind LoudNet(NetInfo info)
        {
            if (info == null || info.m_netAI == null) return FacilityKind.None;
            RoadBaseAI road = info.m_netAI as RoadBaseAI;
            if (road != null)
            {
                if (road is RoadTunnelAI || road is PedestrianZoneRoadAI || info.m_class == null || info.m_class.m_service != ItemClass.Service.Road) return FacilityKind.None;
                return StandingRules.LoudRoad(road.m_noiseAccumulation) ? FacilityKind.Highway : FacilityKind.None;
            }
            // Railways were tried and dropped before release (user decision 2026-09-19): a new station lays track of its own,
            // so the agreement kept fighting its own works. The save format still reads railway data from DEV cities.
            return FacilityKind.None;
        }

        // One opposition per stretch of road works: skip a new segment near a road already opposed (or just opposed here).
        // Same kind only: a highway opposition must not swallow a new railway beside it. A stretch that was just settled
        // (compensated, kept or removed) also stays quiet for a while, so building the promised station - which lays new
        // track of its own - does not open the next opposition at once (user report 2026-09-18).
        private static bool RoadTracked(Vector3 spot, List<Vector3> fresh, FacilityKind kind, long now)
        {
            float r2 = StandingRules.RoadGroupRadius * StandingRules.RoadGroupRadius;
            foreach (Opposition o in oppositions)
            {
                // Only a real settlement quietens a stretch: compensation, a kept agreement or removal. Forcing it through
                // (or letting the deadline pass) does not buy silence (user decision 2026-09-18).
                bool settled = o.Closed > 0 && StandingRules.Settled(o.State) && now - o.Closed < GameClock.Hours(StandingRules.PenaltyDays * 24);
                if (!o.Road || o.Kind != (int)kind || (!Pending(o) && !settled)) continue;
                float dx = o.X - spot.x, dz = o.Z - spot.z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            foreach (Vector3 made in fresh)
            {
                float dx = made.x - spot.x, dz = made.z - spot.z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            return false;
        }

        // New loud road segments next to homes (user request 2026-09-18). Roads are nets, not buildings, so they are
        // tracked by segment id and build index; the whole stretch is represented by one opposition.
        private static void ScanRoads(CityView city, Building[] buildings, long now)
        {
            NetSegment[] segments = Singleton<NetManager>.instance.m_segments.m_buffer;
            List<ushort> fresh = new List<ushort>();
            for (int i = 1; i < segments.Length; i++)
            {
                // Untouchable segments belong to a building (station platforms and their approach tracks), not to new works.
                if ((segments[i].m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted | NetSegment.Flags.Untouchable)) != NetSegment.Flags.Created || LoudNet(segments[i].Info) == FacilityKind.None) continue;
                ushort id = (ushort)i;
                uint index;
                if (knownRoads.TryGetValue(id, out index) && index == segments[i].m_buildIndex) continue;
                knownRoads[id] = segments[i].m_buildIndex;
                if (roadsBaselined) fresh.Add(id);
            }
            // Roads already there when the city loads are accepted, like existing facilities.
            roadsBaselined = true;
            if (!Features.Opposition || fresh.Count == 0) return;
            List<Vector3> made = new List<Vector3>();
            foreach (ushort id in fresh)
            {
                if (oppositions.Count >= 32) break;
                Vector3 spot = segments[id].m_middlePosition;
                FacilityKind kind = LoudNet(segments[id].Info);
                if (kind == FacilityKind.None || RoadTracked(spot, made, kind, now)) continue;
                int homes = HomesNear(buildings, spot, StandingRules.Radius((int)kind));
                if (!StandingRules.Opposes((int)kind, homes)) continue;
                int region = Singleton<DistrictManager>.instance.GetDistrict(spot);
                if (region >= city.Regions.Length || city.Regions[region] == null) region = 0;
                Opposition o = new Opposition { Building = id, BuildIndex = segments[id].m_buildIndex, Road = true, Kind = (int)kind, Region = region, Homes = homes,
                    State = (int)OppositionState.Open, Started = now, Deadline = now + GameClock.Hours(StandingRules.OppositionHours), X = spot.x, Z = spot.z };
                oppositions.Add(o);
                made.Add(spot);
                CivicChirper.PostComplaint(Area(city, region) + " · " + KindName(o.Kind) + (kind == FacilityKind.Railway
                    ? L.T(" · 인근 주민들이 열차 소음을 걱정하며 공사에 반대 의견을 제출했습니다. 행정청이 주민 설명회와 보상 여부를 검토합니다. #시설반대")
                    : L.T(" · 인근 주민들이 도로 소음을 걱정하며 공사에 반대 의견을 제출했습니다. 행정청이 주민 설명회와 보상 여부를 검토합니다. #시설반대")), city, region);
            }
        }

        private static bool roadsBaselined;

        private static byte AirportArea(DistrictManager districts, Vector3 position)
        {
            byte park = districts.GetPark(position);
            return park != 0 && park < districts.m_parks.m_buffer.Length && (districts.m_parks.m_buffer[park].m_flags & DistrictPark.Flags.Created) != 0 && districts.m_parks.m_buffer[park].IsAirport ? park : (byte)0;
        }

        // Runways and taxiways (Airports DLC nets) put an airport area in use as well as buildings.
        private static void MarkRunways(DistrictManager districts, bool[] airportInUse)
        {
            NetSegment[] segments = Singleton<NetManager>.instance.m_segments.m_buffer;
            for (int i = 1; i < segments.Length; i++)
            {
                if ((segments[i].m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) != NetSegment.Flags.Created || segments[i].Info == null) continue;
                NetAI ai = segments[i].Info.m_netAI;
                if (!(ai is AirportAreaRunwayAI) && !(ai is AirportAreaTaxiwayAI)) continue;
                byte park = AirportArea(districts, segments[i].m_middlePosition);
                if (park != 0) airportInUse[park] = true;
            }
        }

        private static void OpposeArea(CityView city, DistrictManager districts, Building[] buildings, byte park, long now)
        {
            foreach (Opposition x in oppositions) if (x.Park == park && Pending(x)) return;
            if (oppositions.Count >= 32) return;
            int homes = HomesNearArea(districts, buildings, park, StandingRules.Radius((int)FacilityKind.Airport));
            if (!StandingRules.Opposes((int)FacilityKind.Airport, homes)) return;
            AddArea(city, districts, park, homes, now);
        }

        private static Opposition AddArea(CityView city, DistrictManager districts, byte park, int homes, long now)
        {
            Vector3 centre = districts.m_parks.m_buffer[park].m_nameLocation;
            int region = districts.GetDistrict(centre);
            if (region >= city.Regions.Length || city.Regions[region] == null) region = 0;
            Opposition o = new Opposition { Park = park, Kind = (int)FacilityKind.Airport, Region = region, Homes = homes, State = (int)OppositionState.Open,
                Started = now, Deadline = now + GameClock.Hours(StandingRules.OppositionHours), X = centre.x, Z = centre.z };
            oppositions.Add(o);
            CivicChirper.PostComplaint(Area(city, region) + " · " + KindName(o.Kind) + L.T(" · 인근 주민들이 시설 설치에 반대 의견을 제출했습니다. 행정청이 주민 설명회와 보상 여부를 검토합니다. #시설반대"), city, region);
            return o;
        }

        // Homes within radius of the airport area's outer edge (or inside it). Falls back to a plain radius around the area's
        // name location when the park grid holds no cells for it (2026-09-18: a DEV test reported 0 homes next to housing).
        private static int HomesNearArea(DistrictManager districts, Building[] buildings, byte park, float radius)
        {
            bool[] near = AreaMask(districts, park, radius);
            if (near == null) return HomesNear(buildings, districts.m_parks.m_buffer[park].m_nameLocation, radius);
            int homes = 0;
            for (int i = 1; i < buildings.Length; i++)
            {
                Building b = buildings[i];
                if ((b.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created || b.Info == null || !(b.Info.m_buildingAI is ResidentialBuildingAI)) continue;
                if (InMask(near, b.m_position.x, b.m_position.z)) homes++;
            }
            return homes;
        }

        // The area's own cells plus the cells its buildings stand on; 0 cells when the grid knows nothing about this park.
        private static bool[] AreaCells(DistrictManager districts, byte park, out int cells)
        {
            int resolution = StandingRules.ParkGridResolution;
            bool[] area = new bool[resolution * resolution];
            cells = 0;
            for (int i = 0; i < area.Length && i < districts.m_parkGrid.Length; i++)
                if (districts.m_parkGrid[i].m_district1 == park) { area[i] = true; cells++; }
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            for (int i = 1; i < buildings.Length; i++)
            {
                if (!CivicSimulation.Live(buildings[i]) || AirportArea(districts, buildings[i].m_position) != park) continue;
                int slot = StandingRules.ParkCell(buildings[i].m_position.z) * resolution + StandingRules.ParkCell(buildings[i].m_position.x);
                if (!area[slot]) { area[slot] = true; cells++; }
            }
            return area;
        }

        // Cells of the area last measured; shown in the DEV window so an empty grid is visible at once.
        internal static int LastAreaCells;

        private static bool[] AreaMask(DistrictManager districts, byte park, float radius)
        {
            int cells;
            bool[] area = AreaCells(districts, park, out cells);
            LastAreaCells = cells;
            return cells == 0 ? null : StandingRules.Surroundings(area, StandingRules.ParkGridResolution, StandingRules.ParkGridCell, radius);
        }

        private static bool InMask(bool[] near, float x, float z) { return near[StandingRules.ParkCell(z) * StandingRules.ParkGridResolution + StandingRules.ParkCell(x)]; }

        // Still waiting on the player or on its consequence: open, agreed, or a protest notice.
        private static bool Pending(Opposition o)
        {
            return o.State == (int)OppositionState.Open || o.State == (int)OppositionState.Agreed || (o.State == (int)OppositionState.Enforced && o.NoticeDue > 0);
        }

        // Parks, trees or health buildings near the facility (or its airport area) for a conditional agreement.
        private static int ConditionCount(Opposition o)
        {
            AgreementCondition condition = (AgreementCondition)o.Condition;
            if (condition == AgreementCondition.None) return 0;
            stationsWithTrack = 0; stationsConnected = 0;
            float radius = StandingRules.ConditionRadius(o.Kind), r2 = radius * radius;
            bool[] near = o.Park != 0 ? AreaMask(Singleton<DistrictManager>.instance, o.Park, radius) : null;
            int count = 0;
            if (condition == AgreementCondition.Trees)
            {
                TreeInstance[] trees = Singleton<TreeManager>.instance.m_trees.m_buffer;
                const ushort live = (ushort)TreeInstance.Flags.Created, mask = (ushort)(TreeInstance.Flags.Created | TreeInstance.Flags.Deleted);
                for (int i = 1; i < trees.Length; i++)
                {
                    if ((trees[i].m_flags & mask) != live) continue;
                    // Same scale as TreeInstance.Position outside the asset editor, without touching ToolManager per tree.
                    if (Near(o, near, trees[i].m_posX * 0.26367188f, trees[i].m_posZ * 0.26367188f, r2)) count++;
                }
                return count;
            }
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            for (int i = 1; i < buildings.Length; i++)
            {
                if (!CivicSimulation.Live(buildings[i]) || buildings[i].Info == null) continue;
                BuildingAI ai = buildings[i].Info.m_buildingAI;
                bool match = condition == AgreementCondition.Park ? ai is ParkAI || ai is ParkBuildingAI
                    : condition == AgreementCondition.Station ? ConnectedStation(buildings[i], ai)
                    : ai is HospitalAI;
                if (match && Near(o, near, buildings[i].m_position.x, buildings[i].m_position.z, r2)) count++;
            }
            return count;
        }

        // A train station counts only once a line actually stops there (user request: "노선이 연결되면 해결").
        // Any passenger station whose own train platform carries a line. The building's item class is not trusted: elevated,
        // mixed and custom stations file themselves under other sub-services, so the platform's own track decides
        // (user report 2026-09-18: a connected elevated station counted as 0).
        private static bool ConnectedStation(Building b, BuildingAI ai)
        {
            if (!(ai is TransportStationAI) || ai is CargoStationAI || b.Info == null) return false;
            NetNode[] nodes = Singleton<NetManager>.instance.m_nodes.m_buffer;
            ushort node = b.m_netNode;
            bool track = false;
            for (int guard = 0; node != 0 && node < nodes.Length && guard < 64; guard++)
            {
                NetInfo info = nodes[node].Info;
                bool rails = info != null && info.m_netAI is TrainTrackBaseAI;
                if (rails)
                {
                    track = true;
                    if (nodes[node].m_transportLine != 0) { stationsWithTrack++; stationsConnected++; return true; }
                }
                node = nodes[node].m_nextBuildingNode;
            }
            if (track) stationsWithTrack++;
            else if (b.Info.m_class != null && b.Info.m_class.m_subService == ItemClass.SubService.PublicTransportTrain) stationsWithTrack++;
            return false;
        }

        // Diagnostics for the DEV window: how many stations were seen near the line and how many carried a line.
        private static int stationsWithTrack, stationsConnected;

        private static bool Near(Opposition o, bool[] near, float x, float z, float r2)
        {
            if (near != null) return InMask(near, x, z);
            float dx = x - o.X, dz = z - o.Z;
            return dx * dx + dz * dz <= r2;
        }

        internal static string ConditionName(int condition)
        {
            switch ((AgreementCondition)condition)
            {
                case AgreementCondition.Park: return L.T("공원 조성");
                case AgreementCondition.Trees: return L.T("방음림(나무 심기)");
                case AgreementCondition.Health: return L.T("건강 대책(병원·진료소)");
                case AgreementCondition.Station: return L.T("기차역 신설·노선 연결");
                default: return "";
            }
        }

        // "Build 1 new park within 400 m of the facility". {0} = metres, {1} = count; Replace, not string.Format, so a
        // community translation with broken braces cannot throw. Airport areas always ask for trees.
        private static string ConditionGoal(Opposition o, int kind, int condition, int needed)
        {
            bool area = o.Park != 0;
            string text;
            switch ((AgreementCondition)condition)
            {
                case AgreementCondition.Park: text = L.T("시설에서 {0}m 안에 공원 {1}곳 새로 짓기"); break;
                case AgreementCondition.Trees: text = area ? L.T("공항 영역 경계에서 {0}m 안에 나무 {1}그루 새로 심기") : o.Road ? L.T("도로에서 {0}m 안에 나무 {1}그루 새로 심기") : L.T("시설에서 {0}m 안에 나무 {1}그루 새로 심기"); break;
                case AgreementCondition.Station: text = L.T("선로에서 {0}m 안에 기차역을 새로 짓고 노선을 연결하기"); break;
                default: text = L.T("시설에서 {0}m 안에 병원·진료소 {1}곳 새로 짓기"); break;
            }
            return text.Replace("{0}", StandingRules.ConditionRadius(kind).ToString("0")).Replace("{1}", needed.ToString());
        }

        private static readonly List<ushort> nearbyHomes=new List<ushort>();
        private static int HomesNear(Building[] buildings, Vector3 position, float radius)
        {
            int homes = 0;
            float r2 = radius * radius;
            BuildingArea.Fill(buildings,Singleton<BuildingManager>.instance.m_buildingGrid,position-Vector3.one*radius,position+Vector3.one*radius,nearbyHomes);
            foreach (ushort i in nearbyHomes)
            {
                Building b = buildings[i];
                if ((b.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created || b.Info == null || !(b.Info.m_buildingAI is ResidentialBuildingAI)) continue;
                float dx = b.m_position.x - position.x, dz = b.m_position.z - position.z;
                if (dx * dx + dz * dz <= r2) homes++;
            }
            return homes;
        }

        private static bool FacilityStands(Opposition o)
        {
            if (o.Road)
            {
                NetSegment[] segments = Singleton<NetManager>.instance.m_segments.m_buffer;
                return o.Building < segments.Length && (segments[o.Building].m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) == NetSegment.Flags.Created && segments[o.Building].m_buildIndex == o.BuildIndex;
            }
            if (o.Park != 0)
            {
                DistrictManager districts = Singleton<DistrictManager>.instance;
                return o.Park < districts.m_parks.m_buffer.Length && (districts.m_parks.m_buffer[o.Park].m_flags & DistrictPark.Flags.Created) != 0 && districts.m_parks.m_buffer[o.Park].IsAirport;
            }
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            return o.Building < buildings.Length && CivicSimulation.Live(buildings[o.Building]) && buildings[o.Building].m_buildIndex == o.BuildIndex;
        }

        private static void Oppose(CityView city, long now)
        {
            if (!Features.Opposition) { oppositions.Clear(); return; }
            for (int i = oppositions.Count - 1; i >= 0; i--)
            {
                Opposition o = oppositions[i];
                bool open = o.State == (int)OppositionState.Open;
                bool notice = o.State == (int)OppositionState.Enforced && o.NoticeDue > 0;
                bool agreed = o.State == (int)OppositionState.Agreed;
                if ((open || notice || agreed) && !FacilityStands(o))
                {
                    o.State = (int)OppositionState.Relocated; o.NoticeDue = 0; o.Closed = now;
                    CivicRecords.ChangeTrust(o.Region, StandingRules.RelocateTrust);
                    CivicChirper.PostComplaint(Area(city, o.Region) + " · " + KindName(o.Kind) + L.T(" · 시설 철거로 주민 반대가 해소되었습니다. #시설반대"), city, o.Region);
                    continue;
                }
                if (open && now >= o.Deadline) { Enforce(city, o, now, false); continue; }
                if (agreed)
                {
                    // Counting trees walks the whole tree buffer, so only every scan (and once more at the deadline).
                    if (o.Progress < 0 || scanTick % 10 == 1 || now >= o.Deadline)
                    {
                        o.Progress = ConditionCount(o);
                        DevStatus = "합의 진행: " + (o.Park != 0 ? "공항 영역 #" + o.Park + " (영역 칸 " + LastAreaCells + (LastAreaCells == 0 ? ", 반경 대체" : "") + ")" : (o.Road ? "구간 #" : "건물 #") + o.Building) +
                            (o.Condition == (int)AgreementCondition.Station ? " · 반경 " + StandingRules.ConditionRadius(o.Kind).ToString("0") + "m 안 기차역 " + stationsWithTrack + "곳, 노선 연결 " + stationsConnected + "곳" : "") +
                            " · " + ConditionName(o.Condition) + " " + StandingRules.Added(o, o.Progress) + "/" + o.Needed + " (합의 시점 " + o.Baseline + ", 현재 " + o.Progress + ")";
                    }
                    if (StandingRules.AgreementMet(o, o.Progress))
                    {
                        o.State = (int)OppositionState.Kept; o.Closed = now;
                        period.SuggestionsKept++;
                        CivicRecords.ChangeTrust(o.Region, StandingRules.AgreementTrust);
                        CivicChirper.PostComplaint(Area(city, o.Region) + " · " + KindName(o.Kind) + L.T(" · 합의 조건을 지켰습니다. 주민 반대가 해소되고 구청 신뢰가 올랐습니다. #시설반대"), city, o.Region);
                    }
                    else if (now >= o.Deadline) Enforce(city, o, now, true);
                    continue;
                }
                if (notice && now >= o.NoticeDue)
                {
                    o.NoticeDue = 0;
                    if (CivicUnrest.ProtestsEnabled) CivicRecords.NextCause = L.T("시설 반대: ") + KindName(o.Kind);
                    CivicUnrest.RequestEvent(city.Generation, o.Region, o.Riot);
                    continue;
                }
                // Keep closed entries only while their happiness penalty can still apply.
                if (!open && !notice && now - o.Closed >= GameClock.Hours(StandingRules.PenaltyDays * 24)) oppositions.RemoveAt(i);
            }
        }

        // broken: a conditional agreement whose condition was not met in time; same result as forcing it through.
        private static void Enforce(CityView city, Opposition o, long now, bool broken)
        {
            o.State = (int)OppositionState.Enforced; o.Closed = now;
            period.Enforced++;
            CivicRecords.ChangeTrust(o.Region, StandingRules.EnforceTrust);
            Enforcement kind = StandingRules.Enforce(o.Kind, Difficulty.Level);
            // Protests switched off in the options: the district is only disappointed.
            if (kind != Enforcement.Penalty && !CivicUnrest.ProtestsEnabled) kind = Enforcement.Penalty;
            string head = Area(city, o.Region) + " · " + KindName(o.Kind) + (broken ? L.T(" · 합의 조건을 지키지 못했습니다") : "");
            if (kind == Enforcement.Penalty)
            {
                CivicChirper.PostComplaint(head + L.T(" · 주민 반대에도 시설 운영을 계속합니다. 인근 지역의 불만이 한동안 이어질 것으로 보입니다. #시설반대"), city, o.Region);
                return;
            }
            // Not a happiness penalty: the protest (or riot) notice is the consequence.
            o.Closed = 1;
            o.Riot = kind == Enforcement.Riot;
            o.NoticeDue = now + GameClock.Hours(CivicRecords.Patience(o.Region) * (o.Riot ? 3 : 6));
            CivicChirper.PostComplaint(head + (o.Riot
                ? L.T(" · 주민 반대에도 시설 운영을 계속하기로 해 격렬한 시위가 예고되었습니다. 설명회·보상이나 시설 철거로 막을 수 있습니다. #집회예고")
                : L.T(" · 주민 반대에도 시설 운영을 계속하기로 해 반대 집회가 예고되었습니다. 설명회·보상이나 시설 철거로 막을 수 있습니다. #집회예고")), city, o.Region);
        }

        // ---- Residents' suggestions ----
        private static void Suggest(CityView city, long now)
        {
            if (!Features.Suggestions) { suggestions.Clear(); forceSuggestion = false; return; }
            for (int i = suggestions.Count - 1; i >= 0; i--)
            {
                Suggestion s = suggestions[i];
                RegionView area = s.Region < city.Regions.Length ? city.Regions[s.Region] : null;
                if (area == null || area.OfficeId == 0) { suggestions.RemoveAt(i); continue; }
                if (s.Accepted && StandingRules.Fulfilled(s.Baseline, Rate(area, s.Region, s.Reason)))
                {
                    suggestions.RemoveAt(i);
                    period.SuggestionsKept++;
                    CivicRecords.ChangeTrust(s.Region, StandingRules.SuggestionKeptTrust);
                    CivicChirper.PostComplaint(area.Name + " · " + Happiness.Reasons[s.Reason] + L.T(" · 주민 건의를 이행했습니다. 구청 신뢰가 올랐습니다. #주민건의"), city, s.Region);
                    continue;
                }
                if (now < s.Deadline) continue;
                suggestions.RemoveAt(i);
                if (!s.Accepted) continue;
                period.SuggestionsBroken++;
                CivicRecords.ChangeTrust(s.Region, StandingRules.SuggestionBrokenTrust);
                CivicChirper.PostComplaint(area.Name + " · " + Happiness.Reasons[s.Reason] + L.T(" · 약속한 주민 건의의 이행 기한이 지났습니다. #주민건의"), city, s.Region);
            }
            bool forced = forceSuggestion;
            forceSuggestion = false;
            if (!forced && (!Features.Suggestions || suggestions.Count >= StandingRules.MaxSuggestions || now < nextSuggestion)) return;
            nextSuggestion = now + GameClock.Hours(6);
            if (!forced && random.NextDouble() >= .35) return;
            List<int> options = new List<int>();
            for (int region = 1; region < city.Regions.Length; region++)
            {
                RegionView area = city.Regions[region];
                if (area == null || area.OfficeId == 0 || area.Residents <= 0) continue;
                foreach (int reason in StandingRules.SuggestionReasons)
                {
                    double rate = Rate(area, region, reason);
                    // Below the complaint threshold: a request, not a grievance.
                    if (rate <= 0 || rate > ComplaintRules.OpenRatio(reason) || CivicComplaints.IsOpen(region, reason)) continue;
                    bool duplicate = false;
                    foreach (Suggestion s in suggestions) if (s.Region == region && s.Reason == reason) duplicate = true;
                    if (!duplicate) options.Add(region * ComplaintRules.Slots + reason);
                }
            }
            if (options.Count == 0) { if (forced) DevStatus = "주민 건의 시험: 구청이 있는 구에 민원 기준 미만의 서비스 부족(교육·의료·치안·소방·대중교통·공원)이 없습니다."; return; }
            int pick = options[random.Next(options.Count)];
            int r = pick / ComplaintRules.Slots, why = pick % ComplaintRules.Slots;
            suggestions.Add(new Suggestion { Region = r, Reason = why, Started = now, Deadline = now + GameClock.Hours(24), Baseline = Rate(city.Regions[r], r, why) });
            nextSuggestion = now + GameClock.Hours(StandingRules.SuggestionCooldownHours);
            if (forced) DevStatus = "주민 건의 발생: " + city.Regions[r].Name + " · " + Happiness.Reasons[why];
            CivicChirper.PostComplaint(city.Regions[r].Name + " · " + Happiness.Reasons[why] + L.T(" · 주민들이 서비스 확충을 건의했습니다. 도시 행정 민원 목록에서 확인할 수 있습니다. #주민건의"), city, r);
        }

        private static double Rate(RegionView area, int region, int reason)
        {
            return ComplaintRules.Rate(CivicComplaints.Affected(area, region, reason), CivicComplaints.Severe(area, region, reason), CivicComplaints.Population(area, region, reason));
        }

        private static string Area(CityView city, int region)
        {
            RegionView area = region >= 0 && region < city.Regions.Length ? city.Regions[region] : null;
            return area == null ? city.CityName : area.Name;
        }

        // ---- Complaint list entries and decisions ----
        internal static void Views(CityView city, DateTime now, List<ComplaintView> list)
        {
            if (preserved != null) return;
            long t = now.Ticks;
            foreach (Opposition o in oppositions)
            {
                bool open = o.State == (int)OppositionState.Open;
                bool notice = o.State == (int)OppositionState.Enforced && o.NoticeDue > 0;
                bool agreed = o.State == (int)OppositionState.Agreed;
                if (!open && !notice && !agreed) continue;
                RegionView area = o.Region < city.Regions.Length ? city.Regions[o.Region] : null;
                int cost = StandingRules.CompensationCost(o.Kind, o.Homes);
                Enforcement kind = StandingRules.Enforce(o.Kind, Difficulty.Level);
                if (kind != Enforcement.Penalty && !CivicUnrest.ProtestsEnabled) kind = Enforcement.Penalty;
                string consequence = kind == Enforcement.Penalty ? L.T("구청 신뢰 하락 · 7일간 지역 행복도 감점") : kind == Enforcement.Protest ? L.T("구청 신뢰 하락 · 반대 집회 예고") : L.T("구청 신뢰 하락 · 격렬한 시위(폭동) 예고");
                int condition = (int)StandingRules.ConditionFor(o.Kind);
                string road = notice ? L.T("예고 시간이 되면 행진이 시작됩니다. 설명회·보상이나 시설 철거로 취소할 수 있습니다.")
                    : agreed ? L.T("기한 안에 지키면 구청 신뢰 +3 · 지키지 못하면: ") + consequence + L.T("\n이행 중에도 설명회·보상으로 바로 끝낼 수 있습니다.")
                    : L.T("응답하지 않거나 강행하면: ") + consequence + L.T("\n조건부 합의(비용 없음, 72시간): ") + ConditionName(condition) + " · " + ConditionGoal(o, o.Kind, condition, StandingRules.Needed(o.Kind));
                list.Add(new ComplaintView { Key = OppositionKeyBase + (o.Road ? RoadKeyOffset + o.Building : o.Park != 0 ? 65536 + o.Park : o.Building), Region = o.Region, Stamp = o.Started, Kind = ComplaintKind.Opposition,
                    Title = Area(city, o.Region) + L.T(" · 시설 반대: ") + KindName(o.Kind),
                    Detail = o.Road ? RoadDetail(o, agreed, cost)
                        : agreed
                        ? ConditionName(o.Condition) + " · " + ConditionGoal(o, o.Kind, o.Condition, o.Needed) + L.T("\n진행: ") + Math.Min(o.Needed, StandingRules.Added(o, Math.Max(o.Progress, o.Baseline))) + " / " + o.Needed + L.T(" · 설명회·보상 비용 ₡") + cost.ToString("N0")
                        : (o.Park != 0 ? L.T("공항 영역 경계에서 ") : L.T("반경 ")) + StandingRules.Radius(o.Kind).ToString("0") + L.T("m 안 주택 ") + o.Homes + L.T("채 · 설명회·보상 비용 ₡") + cost.ToString("N0") + L.T("\n시설을 철거·이전해도 반대가 해소됩니다."),
                    Stage = notice ? (o.Riot ? L.T("격렬한 시위 예고") : L.T("반대 집회 예고")) : agreed ? L.T("조건부 합의 이행 중") : area != null && area.OfficeId != 0 ? area.Name + L.T(" 구청 담당") : L.T("시청 직접 담당"),
                    Road = road, Improving = agreed,
                    Hours = Math.Max(0, ((notice ? o.NoticeDue : o.Deadline) - t) / (double)GameClock.HourTicks),
                    CanPromise = true, CanReject = open, CanAgree = open && condition != 0, HasRoad = true, LocationLabel = o.Kind == (int)FacilityKind.Railway ? L.T("선로 보기") : o.Road ? L.T("도로 보기") : L.T("시설 보기"), Position = !o.Road && o.Park == 0 && FacilityStands(o) ? Singleton<BuildingManager>.instance.m_buildings.m_buffer[o.Building].m_position : new Vector3(o.X, 0, o.Z) });
            }
            foreach (Suggestion s in suggestions)
            {
                RegionView area = s.Region < city.Regions.Length ? city.Regions[s.Region] : null;
                if (area == null) continue;
                list.Add(new ComplaintView { Key = SuggestionKeyBase + s.Region * ComplaintRules.Slots + s.Reason, Region = s.Region, Stamp = s.Started, Kind = ComplaintKind.Suggestion,
                    Improving = s.Accepted,
                    Title = area.Name + L.T(" · 주민 건의: ") + Happiness.Reasons[s.Reason],
                    Detail = s.Accepted ? L.T("추진을 약속했습니다. 기한 안에 해당 서비스가 부족한 주민이 절반 이하로 줄면 구청 신뢰가 오릅니다.")
                        : L.T("민원 기준보다는 적지만 이 서비스가 부족하다는 주민 건의입니다. 수락하고 기한 안에 개선하면 신뢰가 오르고, 지키지 못하면 조금 떨어집니다. 보류하면 영향이 없습니다."),
                    Stage = area.Name + L.T(" 구청 건의"),
                    Road = L.T("현재 서비스 부족 주민 비율 ") + (Rate(area, s.Region, s.Reason) * 100).ToString("0.00") + L.T("% · 건의 당시 ") + (s.Baseline * 100).ToString("0.00") + "%",
                    Hours = Math.Max(0, (s.Deadline - t) / (double)GameClock.HourTicks),
                    CanPromise = !s.Accepted, CanReject = !s.Accepted });
            }
        }

        // Road works read differently from a building: the distance is measured from the road, not from a plot.
        private static string RoadDetail(Opposition o, bool agreed, int cost)
        {
            if (agreed)
                return ConditionName(o.Condition) + " · " + ConditionGoal(o, o.Kind, o.Condition, o.Needed) + L.T("\n진행: ") +
                    Math.Min(o.Needed, StandingRules.Added(o, Math.Max(o.Progress, o.Baseline))) + " / " + o.Needed + L.T(" · 설명회·보상 비용 ₡") + cost.ToString("N0");
            bool rail = o.Kind == (int)FacilityKind.Railway;
            return (rail ? L.T("선로에서 ") : L.T("도로에서 ")) + StandingRules.Radius(o.Kind).ToString("0") + L.T("m 안 주택 ") + o.Homes + L.T("채 · 설명회·보상 비용 ₡") + cost.ToString("N0") +
                (rail ? L.T("\n선로를 철거하거나 지하로 옮겨도 반대가 해소됩니다.") : L.T("\n도로를 철거하거나 지하로 옮겨도 반대가 해소됩니다."));
        }

        // accept: compensation (opposition) or promise (suggestion). reject: enforce (opposition) or set aside (suggestion).
        // agree: conditional agreement (opposition only; accept is ignored).
        internal static void Decide(int key, long stamp, bool accept, bool agree, CityView city, DateTime now)
        {
            if (preserved != null) return;
            long t = now.Ticks;
            if (key >= SuggestionKeyBase)
            {
                if (!Features.Suggestions) return;
                if (agree) return;
                int slot = key - SuggestionKeyBase;
                Suggestion s = suggestions.Find(delegate(Suggestion x) { return x.Region * ComplaintRules.Slots + x.Reason == slot && x.Started == stamp; });
                if (s == null || s.Accepted) return;
                if (!accept) { suggestions.Remove(s); return; }
                s.Accepted = true;
                s.Deadline = t + GameClock.Hours(StandingRules.SuggestionHours);
                return;
            }
            int target = key - OppositionKeyBase;
            if (!Features.Opposition) return;
            Opposition o = oppositions.Find(delegate(Opposition x) {
                bool same = target >= RoadKeyOffset ? x.Road && x.Building == target - RoadKeyOffset
                    : target >= 65536 ? x.Park == target - 65536
                    : !x.Road && x.Park == 0 && x.Building == target;
                return same && x.Started == stamp;
            });
            if (o == null) return;
            bool open = o.State == (int)OppositionState.Open;
            bool notice = o.State == (int)OppositionState.Enforced && o.NoticeDue > 0;
            bool agreed = o.State == (int)OppositionState.Agreed;
            if (agree)
            {
                if (!open || StandingRules.ConditionFor(o.Kind) == AgreementCondition.None) return;
                o.Condition = (int)StandingRules.ConditionFor(o.Kind);
                o.Needed = StandingRules.Needed(o.Kind);
                o.Baseline = o.Progress = ConditionCount(o);
                o.State = (int)OppositionState.Agreed;
                o.Deadline = t + GameClock.Hours(StandingRules.AgreementHours);
                CivicChirper.PostComplaint(Area(city, o.Region) + " · " + KindName(o.Kind) + " · " + ConditionName(o.Condition) +
                    L.T(" · 주민들과 조건부 합의를 맺었습니다. 기한 안에 약속한 조건을 지켜야 합니다. #시설반대"), city, o.Region);
                return;
            }
            if (!accept) { if (open) Enforce(city, o, t, false); return; }
            if (!open && !notice && !agreed) return;
            int cents = StandingRules.CompensationCost(o.Kind, o.Homes) * 100;
            EconomyManager economy = Singleton<EconomyManager>.instance;
            if (economy.PeekResource(EconomyManager.Resource.Construction, cents) != cents)
            {
                CivicChirper.PostComplaint(Area(city, o.Region) + " · " + KindName(o.Kind) + L.T(" · 예산이 부족해 주민 보상을 집행하지 못했습니다. #시설반대"), city, o.Region);
                return;
            }
            economy.FetchResource(EconomyManager.Resource.Construction, cents, ItemClass.Service.Beautification, ItemClass.SubService.None, ItemClass.Level.Level1);
            if (notice) period.Enforced = Math.Max(0, period.Enforced - 1);
            o.State = (int)OppositionState.Compensated; o.NoticeDue = 0; o.Closed = t;
            CivicRecords.ChangeTrust(o.Region, StandingRules.CompensateTrust);
            CivicChirper.PostComplaint(Area(city, o.Region) + " · " + KindName(o.Kind) + (notice
                ? L.T(" · 주민 설명회를 열고 보상하기로 해 예고된 집회를 취소합니다. #시설반대")
                : L.T(" · 주민 설명회를 열고 인근 주민에게 보상하기로 했습니다. #시설반대")), city, o.Region);
        }
    }
}
