using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    // Published to the path-finding thread through a volatile field; never mutated after publication.
    internal sealed class Closure
    {
        private static readonly ushort[] NoRoads = new ushort[0];
        private static readonly uint[] NoIndices = new uint[0];
        internal ushort Road;
        internal uint BuildIndex;
        internal bool DevTraffic;
        // Segments beside the crowd that stopped police cars are physically occupying.
        internal ushort[] Held = NoRoads;
        internal uint[] HeldIndices = NoIndices;
        internal bool Same(ushort road, uint index, ushort[] held, uint[] indices, int count)
        {
            if (Road != road || BuildIndex != index || Held.Length != count) return false;
            for (int i = 0; i < count; i++) if (Held[i] != held[i] || HeldIndices[i] != indices[i]) return false;
            return true;
        }
    }
    internal sealed class UnrestView
    {
        internal long VisualClock;
        internal bool Active, Riot, Police;
        internal bool Announced;
        internal string Announcement;
        internal bool Manual, Elapsed, Resolved, Rebuilt;
        internal bool LocalStage;
        internal bool MeetingPending;
        internal long MeetingStamp;
        internal int Repairs;
        internal double RemainingHours;
        internal double SuppressionMinutes;
        internal Vector3[] DevEscapeDoors;
        internal ushort PoliceVehicle;
        internal int PoliceCount;
        internal DevPoliceUnitView[] DevPoliceUnits;
        internal DevRoadRibbon DevRoad;
        internal long DevSupportStamp;
        internal Vector3 PolicePosition;
        internal string Origin = "", Stage = "", DispatchStatus = "";
        internal Vector3 Position;
        internal string Text = L.T("시위 발생을 기다립니다. 행복도 ‘나쁨’ 이하에서 발생합니다.");
    }

    internal static partial class CivicUnrest
    {
        internal static void Rebase(long now, double factor)
        {
            lock (Gate) {
                state.NextCheck = GameClock.Rebase(state.NextCheck, now, factor);
                state.NextEligible = GameClock.Rebase(state.NextEligible, now, factor);
                if (state.Event != null) {
                    state.Event.Started = GameClock.Rebase(state.Event.Started, now, factor);
                    state.Event.LastDamage = GameClock.Rebase(state.Event.LastDamage, now, factor);
                    state.Event.PoliceAt = GameClock.Rebase(state.Event.PoliceAt, now, factor);
                }
                previousTime = GameClock.Rebase(previousTime, now, factor);
                nextOnward = GameClock.Rebase(nextOnward, now, factor);
                nextDispatch = GameClock.Rebase(nextDispatch, now, factor);
            }
        }
        private static readonly object Gate = new object();
        private static readonly System.Random Random = new System.Random();
        private static UnrestState state = new UnrestState();
        private static MarchSearch search;
        private static MarchSearch onward;
        private static ushort onwardRoad, onwardStart;
        private static uint onwardIndex;
        private static float onwardProgress;
        private static long nextOnward;
        private static int manualRequest, requestGeneration, requestOrigin;
        private static UnrestEvent meetingTarget;
        private static bool meetingAccept;
        private static bool manualSearch, riotSearch;
        private static byte[] preserved;
        private static double protestRate = 1, riotRate = 1;
        private static bool enabled, dispatch;
        private static long previousTime, nextDispatch;
        private static ushort policeTarget;
        private static ushort policeVehicle, policeStation;
        // The leg the crowd just left. Runtime only: after a reload the next 15-minute burst simply
        // skips the behind-the-crowd road until the march advances once more.
        private static ushort passedRoad;
        private static uint passedIndex;
        private static uint policeTargetIndex;
        private static Vector3 position;
        private static int repairs;
        private static string message = "";
        private static volatile Closure closure;
        private static volatile UnrestView view = new UnrestView();
        internal static Closure Closure { get { return closure; } }
        internal static UnrestView View { get { return view; } }

        internal static bool OwnFire(ushort id, uint index)
        {
            lock (Gate)
            {
                foreach (DamageRecord d in state.ProtectedFires)
                    if (d.Building == id && d.BuildIndex == index) return true;
                if (state.Event != null) foreach (DamageRecord d in state.Event.Damage)
                    if (d.Building == id && d.BuildIndex == index) return true;
                return false;
            }
        }

        internal static bool ProtestsEnabled { get { return protestRate > 0; } }
        internal static void SetRates(double protest, double riot) { lock (Gate) { protestRate = protest; riotRate = riot; } }
        internal static void RequestMeeting(long stamp,bool accept) {
            lock(Gate) { if(state.Event==null || state.Event.Meeting!=2 || state.Event.LastDamage!=stamp)return; meetingTarget=state.Event;meetingAccept=accept; }
        }
        internal static void RequestPolice() { lock (Gate) dispatch = true; }
        internal static void RequestEvent(int generation, int origin, bool riot)
        {
            lock (Gate)
            {
                if (!enabled || preserved != null || manualRequest != 0) return;
                requestGeneration = generation; requestOrigin = origin; manualRequest = riot ? 2 : 1;
            }
        }
        internal static void Enable(bool available)
        {
            lock (Gate)
            {
                enabled = available; previousTime = 0;
                if (!available) closure = null;
                if (!available) view = new UnrestView { Text = UnrestBootstrap.Problem == UnrestBootstrap.HarmonyMissing
                    ? L.T("시위 모듈 대기: Harmony 2 모드를 활성화하고 도시를 다시 불러오세요.")
                    : L.T("시위 모듈 중지: 게임이나 다른 모드와 맞지 않는 부분이 있습니다. output_log.txt에서 [CivicPressure]를 확인하세요.") };
            }
        }
        internal static void Reset()
        {
            lock (Gate)
            {
                enabled = false; state = new UnrestState(); search = null; onward = null; nextOnward = 0; preserved = null;
                manualRequest = 0; manualSearch = false; riotSearch = false; meetingTarget=null;
                previousTime = 0; nextDispatch = 0; policeTarget = 0; dispatch = false; repairs = 0;
                policeVehicle = 0; policeStation = 0; passedRoad = 0; passedIndex = 0;
                PoliceDeployment.Reset();
                closure = null; view = new UnrestView(); position = Vector3.zero; message = "";
            }
        }
        internal static void Load(byte[] bytes)
        {
            lock (Gate)
            {
                try
                {
                    state = UnrestSave.Decode(bytes); preserved = null;
                    if (state.Event != null)
                    {
                        position = new Vector3(state.Event.X, state.Event.Y, state.Event.Z);
                        repairs = state.Event.Damage.Length;
                    }
                }
                catch (Exception error)
                {
                    preserved = bytes; state = new UnrestState();
                    Debug.LogError("[CivicPressure] Unrest save rejected and preserved: " + error);
                }
            }
        }
        internal static byte[] Save() { lock (Gate) return preserved ?? UnrestSave.Encode(state); }

        internal static void VehicleReleased(ushort id) { lock (Gate) { PoliceDeployment.Released(id); if (policeVehicle == id) policeVehicle = 0; } }

        internal static void PoliceArrived(ushort vehicle, ushort target, Vector3 vehiclePosition)
        {
            lock (Gate)
            {
                UnrestEvent e = state.Event;
                if (!enabled || e == null || !e.Riot || e.Police || !PoliceDeployment.Owns(vehicle)) return;
                Vector3 delta = new Vector3(e.X, e.Y, e.Z) - vehiclePosition;
                if(!PoliceDeployment.DevIsParked(vehicle)) return;
                e.Police = true;
                PoliceDeployment.Promote(vehicle);
                e.PoliceAt = Singleton<SimulationManager>.instance.m_currentGameTime.Ticks;
                CivicChirper.Post(e, "police");
                message = L.T("경찰 도착 · 현장 진압과 저항 진행 중");
                Debug.Log("[CivicPressure] Riot: dispatched police arrived at the scene.");
            }
        }

        private static RegionView Region(CityView city, int origin) { return origin < 0 ? city.All : city.Regions[origin]; }
        private static double Score(CityView city, int origin)
        {
            RegionView region = Region(city, origin);
            return region == null ? double.NaN : origin < 0 ? city.CityScore : region.Score;
        }

        private static bool TargetExists(UnrestEvent e)
        {
            Building[] buildings=Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            return e.Hall<buildings.Length && UnrestRules.SameTarget(e.Hall,e.HallIndex,e.Hall,buildings[e.Hall].m_buildIndex,CivicSimulation.Assignable(buildings[e.Hall]));
        }
        private static bool CurrentHall(UnrestEvent e,CityView city)
        {
            Building[] buildings=Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            ushort id=city.CityHallId;
            return id<buildings.Length && UnrestRules.SameTarget(e.Hall,e.HallIndex,id,buildings[id].m_buildIndex,CivicSimulation.Assignable(buildings[id]));
        }
        private static ushort FirstHall(CityView city, int origin, bool riot)
        {
            RegionView region = Region(city, origin);
            return !riot && origin > 0 && region != null && region.OfficeId != 0 && !region.OfficeTransferred ? region.OfficeId : city.CityHallId;
        }

        private static void ContinueToCity(UnrestEvent e, CityView city, DateTime now)
        {
            if (!city.CityHallOperating || now.Ticks < nextOnward) return;
            if (onward == null)
            {
                Vector3 p; ushort road;
                if (!UnrestRoads.Position(e, out p, out road)) { message = L.T("시청 이관 대기 · 현재 도로가 없어 경로를 연결할 수 없습니다."); return; }
                ushort node = UnrestRoads.LegNode(e);
                if (node == 0) { message = L.T("시청 이관 대기 · 현재 도로가 없어 경로를 연결할 수 없습니다."); return; }
                NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
                onwardRoad = road; onwardIndex = roads[road].m_buildIndex; onwardStart = node; onwardProgress = e.Progress;
                ushort next = roads[road].m_startNode == node ? roads[road].m_endNode : roads[road].m_startNode;
                onward = new MarchSearch(e.Origin, city.CityHallId, Random, next);
                message = L.T("구청 대응 종료 · 현재 위치에서 시청까지 경로 탐색 중");
            }
            onward.Step();
            if (!onward.Done) return;
            UnrestEvent result = onward.Result; onward = null;
            if (result == null || result.Hall != city.CityHallId || !UnrestRoads.Walkable(onwardRoad) ||
                !CivicSimulation.Live(Singleton<BuildingManager>.instance.m_buildings.m_buffer[result.Hall]) ||
                Singleton<BuildingManager>.instance.m_buildings.m_buffer[result.Hall].m_buildIndex != result.HallIndex ||
                Singleton<NetManager>.instance.m_segments.m_buffer[onwardRoad].m_buildIndex != onwardIndex)
            { message = L.T("시청 이관 대기 · 연결 도로를 확인하세요."); nextOnward = (now.Ticks + GameClock.Hours(1)); return; }
            PoliceDeployment.Release(); policeVehicle = 0;
            ushort[] route = new ushort[result.Roads.Length + 1]; uint[] indices = new uint[route.Length];
            route[0] = onwardRoad; indices[0] = onwardIndex;
            Array.Copy(result.Roads, 0, route, 1, result.Roads.Length); Array.Copy(result.RoadIndices, 0, indices, 1, result.RoadIndices.Length);
            e.Roads = route; e.RoadIndices = indices; e.StartNode = onwardStart; e.Leg = 0; e.Progress = onwardProgress;
            passedRoad = 0; passedIndex = 0;
            e.Hall = result.Hall; e.HallIndex = result.HallIndex; e.Arrived = false; e.Started = now.Ticks;
            message = L.T("구청에서 해결되지 않은 요구를 시청으로 전달합니다.");
            CivicChirper.PostText((Region(city, e.Origin) == null ? L.T("시민") : Region(city, e.Origin).Name + L.T(" 주민")) + L.T("들의 요구 사항이 구청 단계에서 해결되지 않아 시청으로 이관되었습니다. 집회 행렬도 시청 방면으로 이동합니다. #시청 #민원이관"));
        }

        internal static void Tick(CityView city, DateTime now)
        {
            lock (Gate)
            {
                if (!enabled) return;
                if (preserved != null) { view = new UnrestView { Text = L.T("시위 저장 데이터 해석 실패: 원본 보존 중, 새 시위는 중지되었습니다.") }; return; }
                try { Step(city, now); TrafficRouting.Tick(); }
                catch (Exception error)
                {
                    try { PoliceDeployment.Release(); } catch (Exception releaseError) { Debug.LogWarning("[CivicPressure] Police cleanup: " + releaseError.Message); }
                    enabled = false; closure = null; search = null;
                    view = new UnrestView { Text = L.T("시위 모듈 오류: 교통 통제를 해제했습니다. 로그를 확인하세요.") };
                    Debug.LogError("[CivicPressure] Unrest stopped; traffic controls released. " + error);
                }
            }
        }

        private static void Step(CityView city, DateTime now)
        {
            if(city.All==null) {
                view=new UnrestView {Text=L.T("도시 집계 후 저장된 사건·집회 예고를 확인합니다.")};
                return;
            }
#if DEV
            if (DevEnd) { DevEnd = false; DevForceEnd(city, now); }
#endif
            double hours = previousTime == 0 ? 0 : Math.Max(0, Math.Min(1, (now.Ticks - previousTime) / (double)GameClock.HourTicks));
            previousTime = now.Ticks;
            bool hourly = hours > 0 && now.Ticks >= state.NextCheck;
            if (hourly) state.NextCheck = (now.Ticks + GameClock.Hours(1));
            UnrestEvent e = state.Event;
            if (e != null)
            {
                // An event loaded mid-march still carries its walked prefix; collapse it on the first tick
                // so a road repaired behind the crowd cannot invalidate the route afterwards.
                if (e.Leg > 0 && passedRoad == 0) { passedRoad = e.Roads[e.Leg - 1]; passedIndex = e.RoadIndices[e.Leg - 1]; }
                UnrestRoads.Anchor(e);
                CivicRecords.Observe(e, city, now);
            }
            if (e != null && e.DeployedVehicle != 0)
            {
                PoliceDeployment.Tick(e, now);
                policeVehicle = e.DeployedVehicle; policeStation = e.DeployedStation;
                policeTarget = e.Hall; policeTargetIndex = e.HallIndex;
            }
            if (e != null && e.Police && e.PoliceAt == 0) e.PoliceAt = now.Ticks;
            if (hourly && state.ProtectedFires.Length > 0) PreserveFires(null);
            if (manualRequest != 0)
            {
                int request = manualRequest; manualRequest = 0;
                if (requestGeneration != city.Generation) return;
                if (e != null)
                {
                    if (request == 2 && !e.Riot)
                    {
                        e.Riot = true; e.Manual = true; e.Started = now.Ticks; e.LastDamage = e.Riot ? (now.Ticks + GameClock.Hours(-2)) : now.Ticks;
                        message = L.T("수동 폭동 전환 · 경찰 도착 후 진압하면 해산합니다.");
                        CivicChirper.Post(e, "escalation");
                    }
                    else message = L.T("이미 진행 중인 시위 또는 폭동이 있습니다.");
                }
                else if (search != null) message = L.T("행진 경로를 탐색하고 있습니다. 잠시 기다려 주세요.");
                else if (!city.HasCityHall) message = L.T("먼저 시청을 지정하세요.");
                else if (!city.CityHallOperating) message = L.T("시청 청사를 복구하거나 다시 지정해야 합니다.");
                else if (requestOrigin < -1 || requestOrigin >= city.Regions.Length ||
                    Region(city, requestOrigin) == null || Region(city, requestOrigin).Residents <= 0)
                    message = L.T("시민 집계가 완료된 지역을 선택하세요.");
                else
                {
                    CivicRecords.Notice = null;
                    manualSearch = true; riotSearch = request == 2;
                    search = new MarchSearch(requestOrigin, FirstHall(city, requestOrigin, riotSearch), Random);
                    message = L.T("수동 발생: 담당 행정청으로 이어지는 행진 경로 탐색 중…");
                }
            }
            if (e == null)
            {
                closure = null;
                if (CivicRecords.Notice != null && (!CivicRecords.Needed(CivicRecords.Notice, city) || !CivicComplaints.Ready(CivicRecords.Notice.Origin, now.Ticks) || protestRate <= 0))
                {
                    CivicChirper.PostText(CivicRecords.Notice.Area + L.T(" 집회 예고를 취소합니다. 생활 여건 개선 또는 발생 조건 변경이 확인되었습니다. #집회취소"));
                    CivicRecords.Notice = null; if (!manualSearch) search = null;
                    state.NextEligible = (now.Ticks + GameClock.Hours(6)); message = L.T("집회 예고 취소");
                }
                if (search != null)
                {
                    search.Step();
                    if (search.Done)
                    {
                        e = search.Result; search = null;
                        Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                        if (e != null && Region(city,e.Origin)!=null && city.CityHallOperating && CivicSimulation.Assignable(buildings[e.Hall]) && FirstHall(city, e.Origin, riotSearch) == e.Hall && buildings[e.Hall].m_buildIndex == e.HallIndex &&
                            (manualSearch || (UnrestRules.CanProtest(Score(city, e.Origin)) && protestRate > 0 && CivicComplaints.Ready(e.Origin, now.Ticks))))
                        {
                            e.Manual = manualSearch; e.Riot = riotSearch;
                            CivicRecords.Begin(e, city, now, false);
                            CivicRecords.Notice = null;
                            e.Started = now.Ticks; e.LastDamage = e.Riot ? (now.Ticks + GameClock.Hours(-2)) : now.Ticks; state.Event = e; repairs = 0; policeTarget = 0; policeVehicle = 0; nextDispatch = 0; passedRoad = 0; passedIndex = 0;
                            message = e.Manual ? L.T("수동 발생") : L.T("시민들이 담당 행정청을 향해 행진합니다.");
                            CivicStanding.Incident(e.Riot);
                            Debug.Log("[CivicPressure] Protest started: origin=" + e.Origin + ", hall=" + e.Hall + ", roads=" + e.Roads.Length);
                            if (!e.Riot && e.Hall != city.CityHallId) CivicChirper.PostText(Region(city, e.Origin).Name + L.T(" 주민 집회 행렬이 구청 방면으로 이동을 시작했습니다. 해당 구간 통행에 협조하여 주시기 바랍니다. #구청 #집회안내"));
                            else CivicChirper.Post(e, e.Riot ? "riot" : "protest");
                        }
                        else { e = null; CivicRecords.Notice = null; message = L.T("행진 가능한 연결 도로 또는 발생 조건을 찾지 못했습니다."); state.NextEligible = (now.Ticks + GameClock.Hours(6)); }
                        manualSearch = false; riotSearch = false;
                    }
                }
                else if (CivicRecords.Notice != null)
                {
                    if (now.Ticks >= CivicRecords.Notice.Due && city.CityHallOperating) search = new MarchSearch(CivicRecords.Notice.Origin, FirstHall(city, CivicRecords.Notice.Origin, false), Random);
                }
                else if (hourly && now.Ticks >= state.NextEligible && city.CityHallOperating && city.All != null && protestRate > 0 && CivicRecords.Available)
                {
                    List<int> candidates = new List<int>();
                    bool districts = false;
                    for (int i = 1; i < city.Regions.Length; i++) if (city.Regions[i] != null) { districts = true; break; }
                    if (!districts) { if (UnrestRules.CanProtest(city.CityScore) && CivicComplaints.Ready(-1, now.Ticks)) candidates.Add(-1); }
                    else for (int i = 0; i < city.Regions.Length; i++)
                        if (city.Regions[i] != null && UnrestRules.CanProtest(city.Regions[i].Score) && CivicComplaints.Ready(i, now.Ticks)) candidates.Add(i);
                    // Trusted districts protest less (CivicStanding): pick the district first, then roll with its trust.
                    int origin = candidates.Count > 0 ? candidates[Random.Next(candidates.Count)] : -2;
                    if (origin != -2 && Random.NextDouble() < UnrestRules.Chance(0.12, protestRate * Difficulty.Frequency * StandingRules.ProtestFactor(origin > 0 ? CivicRecords.Trust(origin) : 50), 1))
                    {
                        CivicRecords.Notice = CivicComplaints.PrepareNotice(origin, city, now);
                        if (CivicRecords.Notice != null) CivicChirper.PostComplaint(CivicRecords.NoticeText(city, now) + L.T(" · 신고된 생활 문제가 개선될 경우 집회는 취소됩니다. #집회예고"), city, origin);
                        message = L.T("집회 사전 예고 · 원인 해결을 기다립니다.");
                    }
                }
                if (e == null)
                {
                    view = new UnrestView { Announced = CivicRecords.Notice != null, Announcement = CivicRecords.NoticeText(city, now), Text = search != null ? L.T("행진 경로 탐색 중…") :
                        message.Length > 0 ? message : L.T("활성 시위 없음 · 행복도 ‘나쁨’ 이하에서 발생합니다.") };
                    return;
                }
            }

            ushort roadId;
            RegionView region = Region(city, e.Origin);
            bool resolved = !e.Manual && ProtestDemands.Resolved(region);
            bool elapsed = now.Ticks - e.Started >= GameClock.Hours(48);
            double officeHours = Difficulty.OfficeHours * CivicRecords.Patience(e.Origin) + DelegationMeeting.Bonus(e);
            bool localStage = !CurrentHall(e,city);
            long meetingDeadline=e.LastDamage+GameClock.Hours(officeHours);
            bool meetingLocal=localStage && TargetExists(e) && FirstHall(city,e.Origin,false)==e.Hall;
            if(e.Meeting==0 && meetingLocal && e.Arrived && !e.Riot && now.Ticks<meetingDeadline) {
                if(DelegationMeeting.Roll(e,true,Random.NextDouble()))
                    CivicChirper.PostComplaint(L.T("시위 대표단이 면담을 요청했습니다. 도시 행정 시위 화면에서 수락·거절할 수 있습니다. #대표단면담"), city, e.Origin);
            }
            if(e.Meeting==2 && (e.Riot || !meetingLocal || !e.Arrived || now.Ticks>=meetingDeadline))e.Meeting=5;
            if(meetingTarget!=null) {
                if(object.ReferenceEquals(meetingTarget,e) && DelegationMeeting.Decide(e,meetingLocal,now.Ticks,meetingDeadline,meetingAccept)) {
                    message=meetingAccept ? L.T("대표단 면담 수락 · 시청 행진을 3게임 시간 유예합니다.") : L.T("대표단 면담 거절 · 기존 시위 절차를 유지합니다.");
                    CivicChirper.PostComplaint(message, city, e.Origin);
                    officeHours=Difficulty.OfficeHours*CivicRecords.Patience(e.Origin)+DelegationMeeting.Bonus(e);
                }
                meetingTarget=null;
            }
            bool transfer = localStage && (e.Riot || !TargetExists(e) || FirstHall(city, e.Origin, false) != e.Hall ||
                (e.Arrived && now.Ticks - e.LastDamage >= GameClock.Hours(officeHours)));
            if (transfer && (e.Riot || !resolved) && !e.Police) ContinueToCity(e, city, now);
            localStage = !CurrentHall(e,city);
            elapsed = now.Ticks - e.Started >= GameClock.Hours(48);
            if (localStage && !e.Riot && e.Arrived)
                elapsed = now.Ticks - e.LastDamage >= GameClock.Hours(officeHours + 48);
            Vector3 current;
            bool routeValid = UnrestRoads.Position(e, out current, out roadId);
            if (routeValid)
            {
                position = current;
                NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
                if (!e.Arrived && !e.Police && !transfer && hours > 0 && (roads[roadId].m_flags & NetSegment.Flags.Collapsed) == 0)
                {
                    float remaining = (float)hours * UnrestRules.Speed(e.Riot);
                    if(e.Riot) {
                        DevCrowdPresence crowd=DevCrowdPresence.Current;
                        DevRoadRibbon ribbon=view.DevRoad;
                        if(crowd!=null && crowd.Stamp==e.Started && crowd.HasCenter && ribbon!=null) {
                            float actual,side; ribbon.Project(crowd.Center,out actual,out side);
                            remaining=DevRiotCrowdRules.SimulationAdvance(remaining,actual,ribbon.Front);
                        } else remaining=0;
                    }
                    while (remaining > 0)
                    {
                        float length = Math.Max(1, roads[roadId].m_averageLength);
                        float end = e.Leg == e.Roads.Length - 1 ? 0.5f : 1;
                        float distance = Math.Max(0, (end - e.Progress) * length);
                        if (remaining < distance) { e.Progress += remaining / length; break; }
                        remaining -= distance;
                        if (e.Leg == e.Roads.Length - 1)
                        {
                            e.Progress = 0.5f; e.Arrived = true;
                            if (!e.Riot)
                            {
                                e.LastDamage = now.Ticks;
                                if (localStage) CivicChirper.PostText((region == null ? L.T("지역") : region.Name) + L.T(" 집회 인원이 구청 앞에 도착해 농성에 들어갔습니다. 기한 내 개선되지 않을 경우 시청으로 이관됩니다. #구청 #농성"));
                                else CivicChirper.Post(e, "sit-in");
                            }
                            break;
                        }
                        // Advance only after validating the next segment, preserving a last valid location.
                        int oldLeg = e.Leg; float oldProgress = e.Progress;
                        ushort passed = e.Roads[oldLeg]; uint passedAt = e.RoadIndices[oldLeg];
                        e.Leg++; e.Progress = 0;
                        if (!UnrestRoads.Position(e, out current, out roadId) || !UnrestRoads.Walkable(roadId))
                        { e.Leg = oldLeg; e.Progress = oldProgress; message = L.T("행진 경로가 끊겨 대기 중입니다."); break; }
                        if (e.Riot && !e.Police)
                        {
                            DamagePassedRoad(e, passed, passedAt);
                        }
                        // Drop the legs already walked before the riot can wreck them.
                        UnrestRoads.Anchor(e);
                        passedRoad = passed; passedIndex = passedAt;
                    }
                    routeValid = UnrestRoads.Position(e, out current, out roadId);
                    if (routeValid) position = current;
                }
            }
            PublishClosure(e);
            e.X = position.x; e.Y = position.y; e.Z = position.z;
            if (!routeValid) message = closure != null
                ? L.T("도로가 교체되어 행진을 중단했습니다. 현재 위치의 교통 통제와 해산 조건은 유지됩니다.")
                : L.T("도로가 교체되어 행진을 중단했습니다. 해산 조건은 유지됩니다.");

            // Disband before escalation: a protest whose demands were met cannot become a riot that tick.
            DevCombatResult combat=DevCombatResult.Current;
            bool combatReady=DevCombatResult.Matches(combat,e.Started,e.DeployedVehicle);
            bool policeLost=combatReady && !combat.PoliceWon;
            bool policeWon=combatReady && combat.PoliceWon;
            if (policeLost)
            {
                PoliceDeployment.DevDefeat(e);
                PoliceDeployment.Release();
                e.Police = false; e.PoliceAt = 0; e.DeployedVehicle = e.DeployedStation = 0; e.StationIndex = 0;
                policeVehicle = policeStation = policeTarget = 0; dispatch = false; nextDispatch = 0;
                e.LastDamage = (now.Ticks + GameClock.Minutes(-15));
                message = L.T("진압 실패 · 경찰 철수. 다른 경찰차의 출동을 다시 요청하세요.");
                CivicChirper.PostText(L.T("폭동 현장에 투입된 경찰이 진압에 실패해 철수했습니다. 추가 경찰력 투입을 검토 중이며, 현장 접근을 삼가시기 바랍니다. #진압실패 #현장통제"));
                Debug.Log("[CivicPressure] Suppression failed; deployed car released and new dispatch required.");
                if (CivicRecords.Draft != null) CivicRecords.Draft.Failures++;
            }
            if (e.Riot ? policeWon : UnrestRules.Disbands(false, elapsed, resolved, true, false))
            {
                PreserveFires(e);
                if(e.Riot && policeWon) CivicComplaints.DevOverforce(city,e.Origin,now);
                CivicRecords.Finish(e, city, now, e.Riot ? L.T("경찰 진압 후 해산") : resolved ? L.T("지역 문제 해결 후 해산") : L.T("시간 경과로 해산 · 남은 민원은 유지"));
                PoliceDeployment.Release();
                state.Event = null; state.NextEligible = (now.Ticks + GameClock.Hours(24)); closure = null; policeTarget = 0;
                passedRoad = 0; passedIndex = 0;
                onward = null; nextOnward = 0; CivicComplaints.End(e.Origin, now.Ticks);
                message = e.Riot ? L.T("폭동 해산: 경찰 도착 후 진압을 마쳤습니다.") : L.T("시위가 자진 해산했습니다.");
                CivicChirper.Post(e, e.Riot ? "riot-end" : "protest-end");
                view = new UnrestView { Text = message };
                Debug.Log("[CivicPressure] " + message); return;
            }
            if (hourly && !e.Riot && UnrestRules.CanRiot(Score(city, e.Origin)) && Random.NextDouble() < UnrestRules.Chance(0.03, riotRate * Difficulty.Frequency, 1))
            {
                e.Riot = true; e.LastDamage = (now.Ticks + GameClock.Hours(-2));
                message = L.T("시위가 폭동으로 전환되었습니다. 경찰 출동을 요청해 진압하세요.");
                Debug.Log("[CivicPressure] Protest escalated to riot: origin=" + e.Origin);
                CivicStanding.Incident(true);
                CivicChirper.Post(e, "escalation");
            }
            if (e.Riot)
            {
                if (dispatch) { dispatch = false; Dispatch(now); }
                if (hourly)
                {
                    CheckRepairs(e);
                }
                    if (routeValid && now.Ticks - e.LastDamage >= GameClock.Minutes(15))
                    {
                        Damage(e, city, roadId, 0);
                        e.LastDamage = now.Ticks;
                    }
            }
            else dispatch = false;
            string originName = e.Origin < 0 ? L.T("시 전체") : region == null ? L.T("변경된 지구") : region.Name;
            Vector3 policePosition = Vector3.zero;
            if (policeVehicle != 0)
            {
                Vehicle car = Singleton<VehicleManager>.instance.m_vehicles.m_buffer[policeVehicle];
                if ((!e.Police && car.m_targetBuilding != policeTarget) || (car.m_flags & Vehicle.Flags.Created) == 0 || car.m_sourceBuilding != policeStation || car.Info == null || !(car.Info.m_vehicleAI is PoliceCarAI)) policeVehicle = 0;
                else policePosition = car.GetLastFramePosition();
            }
            view = new UnrestView { VisualClock = now.Ticks, Active = true, Riot = e.Riot, Police = e.Police, Position = position,
                DevPoliceUnits = PoliceDeployment.DevArrived(e),
                DevSupportStamp = devArmedStamp,
                DevEscapeDoors = DevDoors(e),
                DevRoad = routeValid ? DevRoadCorridor.Make(e, position) : null,
                PoliceVehicle = policeVehicle, PolicePosition = policePosition, PoliceCount = e.DeployedVehicle == 0 ? 0 : 1 + e.Support.Length,
                SuppressionMinutes = e.Police ? Math.Max(0, 30 - (now.Ticks - e.PoliceAt) / (double)GameClock.MinuteTicks) : 30,
                Manual = e.Manual, Elapsed = elapsed, Resolved = resolved, Rebuilt = RepairsComplete(e), Repairs = repairs, LocalStage = localStage,
                RemainingHours = localStage && e.Arrived ? Math.Max(0, officeHours - (now.Ticks - e.LastDamage) / (double)GameClock.HourTicks) : Math.Max(0, 48 - (now.Ticks - e.Started) / (double)GameClock.HourTicks),
                MeetingPending = e.Meeting==2 && localStage && !e.Riot, MeetingStamp=e.LastDamage,
                DispatchStatus = e.Meeting==2 && !e.Riot ? L.T("대표단 면담 요청 · 수락하면 시청 행진 3게임 시간 유예") : message, Origin = originName, Stage = e.Riot ? (e.Police ? L.T("군중 대치 · 진압 중") : L.T("폭동 진행 중")) : localStage ? (e.Arrived ? L.T("구청 앞 농성 · 시청 이관까지 ") + Math.Max(0, officeHours - (now.Ticks - e.LastDamage) / (double)GameClock.HourTicks).ToString("0.0") + L.T("시간") : L.T("구청으로 행진 중")) : e.Arrived ? L.T("시청 앞 농성") : L.T("시청으로 행진 중"),
                Text = originName + " · " + (e.Riot ? L.T("폭동") : e.Arrived ? L.T("시청 앞 농성") : L.T("시청으로 행진")) +
                "\n" + (e.Riot ? (e.Police ? DevSuppressionText() : L.T("경찰 도착 대기")) : elapsed ? L.T("시간 조건 충족") : L.T("자진 해산까지 ") + Math.Max(0, 48 - (now.Ticks - e.Started) / (double)GameClock.HourTicks).ToString("0.0") + L.T(" 게임 시간")) +
                (e.Riot ? L.T(" · 미복구 ") + repairs + L.T("곳 · 경찰 ") + (e.Police ? L.T("도착") : L.T("미도착")) : "") + "\n" + message };
        }

        private static long devDoorsStamp;
        private static ushort devDoorsLead;
        private static Vector3[] devDoors = new Vector3[0];
        private static Vector3[] DevDoors(UnrestEvent e)
        {
            if(e.Police && (devDoorsStamp!=e.Started || devDoorsLead!=e.DeployedVehicle)) {
                devDoorsStamp=e.Started; devDoorsLead=e.DeployedVehicle;
                devDoors=PoliceDeployment.DevEscapeDoors(e,DevRoadCorridor.Make(e,new Vector3(e.X,e.Y,e.Z)));
            }
            return devDoors;
        }
        private static string DevSuppressionText()
        {
            return L.T("현장 진압 중 · 남은 저항 인원과 경찰 상태로 승패 결정");
        }

        internal static long DevRiotStamp
        {
            get { lock (Gate) return state.Event != null && state.Event.Riot ? state.Event.Started : 0; }
        }
        private static bool RepairsComplete(UnrestEvent e) { return !e.Riot || (e.Damage.Length == 0 && repairs == 0); }

        // Scratch buffers reused every tick; only their copies reach the published closure.
        private static readonly ushort[] heldRoads = new ushort[3];
        private static readonly uint[] heldIndices = new uint[3];

        // Traffic control follows the segment the crowd is standing on, not the whole marched route:
        // repairing a road the riot destroyed earlier must not silently reopen the crowd's own road.
        // While police hold the scene their stopped cars occupy neighbouring segments, so those are
        // closed too and approaching traffic keeps looking for a legal U-turn or detour.
        private static void PublishClosure(UnrestEvent e)
        {
            ushort road = 0; uint index = 0;
            if (e.Leg >= 0 && e.Leg < e.Roads.Length && e.Leg < e.RoadIndices.Length)
            {
                NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
                road = e.Roads[e.Leg]; index = e.RoadIndices[e.Leg];
                if (road == 0 || road >= roads.Length || roads[road].m_buildIndex != index ||
                    (roads[road].m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) != NetSegment.Flags.Created) road = 0;
            }
            if (road == 0) { if (closure != null) closure = null; return; }
            int count = PoliceDeployment.Occupied(e, road, heldRoads, heldIndices);
            Closure published = closure;
            if (published != null && published.DevTraffic != (e.Riot && !e.Police)) published = null;
            if (published != null && published.Same(road, index, heldRoads, heldIndices, count)) return;
            ushort[] held = new ushort[count]; uint[] indices = new uint[count];
            Array.Copy(heldRoads, held, count); Array.Copy(heldIndices, indices, count);
            Closure next = new Closure { Road = road, BuildIndex = index, Held = held, HeldIndices = indices };
            next.DevTraffic = e.Riot && !e.Police;
            closure = next;
        }

        private static void PreserveFires(UnrestEvent e)
        {
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            List<DamageRecord> fires = new List<DamageRecord>(state.ProtectedFires);
            if (e != null) fires.AddRange(e.Damage);
            fires.RemoveAll(delegate(DamageRecord d) { return d.Building >= buildings.Length || !CivicSimulation.Live(buildings[d.Building]) || buildings[d.Building].m_buildIndex != d.BuildIndex || buildings[d.Building].m_fireIntensity == 0; });
            state.ProtectedFires = fires.ToArray();
        }

        private static readonly List<ushort> dispatchStations=new List<ushort>();
        private static bool[] dispatchExisting=new bool[0];
        private static int[] dispatchUsed=new int[0];
        private static void PrepareDispatch()
        {
            Building[] buildings=Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            Vehicle[] cars=Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            dispatchStations.Clear();
            for(int i=1;i<buildings.Length;i++) if(buildings[i].Info!=null && buildings[i].Info.m_buildingAI is PoliceStationAI) dispatchStations.Add((ushort)i);
            if(dispatchExisting.Length!=cars.Length) dispatchExisting=new bool[cars.Length];
            else Array.Clear(dispatchExisting,0,dispatchExisting.Length);
            if(dispatchUsed.Length!=buildings.Length) dispatchUsed=new int[buildings.Length];
            else Array.Clear(dispatchUsed,0,dispatchUsed.Length);
            for(int i=1;i<cars.Length;i++) {
                dispatchExisting[i]=(cars[i].m_flags & Vehicle.Flags.Created)!=0 && cars[i].Info!=null && cars[i].Info.m_vehicleAI is PoliceCarAI;
                if(dispatchExisting[i] && cars[i].m_sourceBuilding<dispatchUsed.Length) dispatchUsed[cars[i].m_sourceBuilding]++;
            }
        }
        private static int StationPolice(ushort station,Vehicle[] cars)
        {
            ushort id=Singleton<BuildingManager>.instance.m_buildings.m_buffer[station].m_ownVehicles;
            int count=0;
            for(int guard=0;id!=0 && id<cars.Length && guard<cars.Length;guard++) {
                Vehicle car=cars[id];
                if((car.m_flags & Vehicle.Flags.Created)!=0 && car.m_sourceBuilding==station && car.Info!=null && car.Info.m_vehicleAI is PoliceCarAI) count++;
                id=car.m_nextOwnVehicle;
            }
            if(id!=0) { // Corrupt ownership chain: retain the original conservative whole-buffer count.
                count=0; for(int i=1;i<cars.Length;i++) if((cars[i].m_flags & Vehicle.Flags.Created)!=0 && cars[i].m_sourceBuilding==station && cars[i].Info!=null && cars[i].Info.m_vehicleAI is PoliceCarAI) count++;
            }
            return count;
        }
        private static ushort NewPolice(ushort station,ushort target,Vehicle[] cars)
        {
            ushort id=Singleton<BuildingManager>.instance.m_buildings.m_buffer[station].m_ownVehicles, found=0;
            for(int guard=0;id!=0 && id<cars.Length && guard<cars.Length;guard++) {
                Vehicle car=cars[id];
                if(!dispatchExisting[id] && (car.m_flags & Vehicle.Flags.Created)!=0 && car.m_sourceBuilding==station && car.m_targetBuilding==target && car.Info!=null && car.Info.m_vehicleAI is PoliceCarAI && (found==0 || id<found)) found=id;
                id=car.m_nextOwnVehicle;
            }
            if(found!=0) return found;
            // Optional service mods may not link ownership immediately.
            for(int i=1;i<cars.Length;i++) if(!dispatchExisting[i] && (cars[i].m_flags & Vehicle.Flags.Created)!=0 && cars[i].m_sourceBuilding==station && cars[i].m_targetBuilding==target && cars[i].Info!=null && cars[i].Info.m_vehicleAI is PoliceCarAI) return (ushort)i;
            return 0;
        }
        private static void Dispatch(DateTime now)
        {
            UnrestEvent e = state.Event;
            if (e == null) return;
            if (e.DeployedVehicle != 0 && PoliceDeployment.Valid(e)) { message = L.T("경찰차 ") + (1 + e.Support.Length) + L.T("대가 이미 출동 중입니다."); return; }
            if (now.Ticks < nextDispatch) { message = L.T("잠시 뒤 다시 요청하세요."); return; }
            int requested = 3;
            PrepareDispatch();
            for (int i = 0; i < requested; i++)
            {
                int before = e.DeployedVehicle == 0 ? 0 : 1 + e.Support.Length;
                nextDispatch = 0; DispatchOne(now);
                int after = e.DeployedVehicle == 0 ? 0 : 1 + e.Support.Length;
                if (after == before) break;
            }
            int total = e.DeployedVehicle == 0 ? 0 : 1 + e.Support.Length;
            if (total > 0) {
                policeVehicle = e.DeployedVehicle; policeStation = e.DeployedStation;
                policeTarget = e.Hall; policeTargetIndex = e.HallIndex;
                message = L.T("경찰차 ") + total + "/" + requested + L.T("대 출동") + (total < requested ? L.T(" · 가용 차량 부족 또는 배차 실패") : L.T(" · 군중 현장으로 이동 중"));
                nextDispatch = (now.Ticks + GameClock.Minutes(10));
            }
        }
        private static long devArmedStamp;
        internal static void DevResetArmedSupport() { devArmedStamp=0; }
        internal static int DevArmedSupport(long stamp)
        {
            UnrestEvent e=state.Event;
            if(e==null || !e.Riot || e.Started!=stamp || devArmedStamp==stamp || DevCombatResult.Resolving) return 0;
            PrepareDispatch();
            dispatchStations.Remove(e.DeployedStation);
            foreach(PoliceUnit unit in e.Support) dispatchStations.Remove(unit.Station);
            int added=PoliceDeployment.DevRecruitSupport(dispatchStations,5);
            long previous=nextDispatch;
            DateTime now=Singleton<SimulationManager>.instance.m_currentGameTime;
            for(int i=added;i<5;i++) {
                int before=e.Support.Length;
                nextDispatch=0; DispatchOne(now);
                if(e.Support.Length==before) break;
                PoliceUnit unit=e.Support[e.Support.Length-1];
                PoliceDeployment.DevArmedCars[unit.Vehicle]=true;
                added++;
            }
            nextDispatch=Math.Max(previous,nextDispatch);
            if(added>0) devArmedStamp=stamp;
            return added;
        }
        private static void DispatchOne(DateTime now)
        {
            if (now.Ticks < nextDispatch) { message = L.T("경찰 요청 대기 중입니다. 잠시 뒤 다시 요청할 수 있습니다."); return; }
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            UnrestEvent incident = state.Event;
            if (incident == null) return;
            ushort target = incident.Hall;
            if (target >= buildings.Length || !UnrestRules.SameTarget(incident.Hall,incident.HallIndex,target,buildings[target].m_buildIndex,CivicSimulation.Live(buildings[target])))
            { message = L.T("출동할 수 없습니다. 시청 지정 상태를 확인하세요."); return; }
            Vehicle[] vehicles = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            ushort station = 0; float nearest = float.MaxValue;
            foreach (ushort i in dispatchStations)
            {
                Building b = buildings[i];
                if (!Healthy(b) || b.Info == null || (b.m_flags & (Building.Flags.Active | Building.Flags.Evacuating)) != Building.Flags.Active) continue;
                PoliceStationAI ai = b.Info.m_buildingAI as PoliceStationAI;
                if (ai == null || b.Info.m_class.m_level >= ItemClass.Level.Level4) continue;
                int budget = Singleton<EconomyManager>.instance.GetBudget(b.Info.m_class);
                int limit = (PlayerBuildingAI.GetProductionRate(b.m_productionRate, budget) * ai.PoliceCarCount + 99) / 100;
                float distance = (b.m_position - position).sqrMagnitude;
                if (distance < nearest && Math.Max(dispatchUsed[i],StationPolice(i,vehicles)) < limit) { station = i; nearest = distance; }
            }
            if (station == 0) { message = L.T("배차 불가: 가용 차량이 있는 운영 중 경찰서가 없습니다."); return; }
            TransferManager.TransferOffer offer = new TransferManager.TransferOffer {
                Building = target, Position = buildings[target].m_position, Priority = 7, Amount = 1, Active = false };
            policeTarget = target; policeTargetIndex = buildings[target].m_buildIndex;
            buildings[station].Info.m_buildingAI.StartTransfer(station, ref buildings[station], TransferManager.TransferReason.Crime, offer);
            ushort created=NewPolice(station,target,vehicles);
            if(created!=0)
            {
                ushort i=created; dispatchExisting[i]=true;
                dispatchUsed[station]++;
                vehicles[i].m_flags |= Vehicle.Flags.Emergency2;
                policeVehicle = (ushort)i; policeStation = station;
                PoliceDeployment.Assign(incident, policeVehicle, station);
                if (CivicRecords.Draft != null) CivicRecords.Draft.Cars++;
                nextDispatch = (now.Ticks + GameClock.Minutes(10));
                message = L.T("경찰서 #") + station + L.T(" 배차 완료 · 경찰차 #") + i + L.T(" 출동 중");
                Debug.Log("[CivicPressure] " + message + ", target=" + target);
                return;
            }
            policeTarget = 0;
            message = L.T("배차 실패: 경찰차 생성 또는 경로 설정 실패. 경찰서와 도로를 확인하세요.");
        }
        private static bool Healthy(Building b)
        {
            return CivicSimulation.Live(b) && b.m_fireIntensity == 0 &&
                (b.m_flags & (Building.Flags.Completed | Building.Flags.Collapsed | Building.Flags.BurnedDown | Building.Flags.Abandoned)) == Building.Flags.Completed;
        }
        private static bool Zoned(ItemClass.Service service)
        {
            return service == ItemClass.Service.Residential || service == ItemClass.Service.Commercial ||
                service == ItemClass.Service.Industrial || service == ItemClass.Service.Office;
        }
        private static readonly List<ushort> damageCandidates=new List<ushort>(), repairCandidates=new List<ushort>();
        private static void Damage(UnrestEvent e, CityView city, ushort roadId, int side)
        {
            if (e.TotalDamage >= UnrestRules.MaxBuildingDamage) return;
            DevCrowdPresence crowd=DevCrowdPresence.Current;
            if(!DevBuildingDamage.Fresh(crowd,e.Started,Singleton<SimulationManager>.instance.m_currentFrameIndex)) return;
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            int kind = UnrestRules.DamageKind(Random.NextDouble());
            if (kind != 0)
            {
                Vector3[] points = null;
                if (side != 0)
                {
                    NetSegment road = Singleton<NetManager>.instance.m_segments.m_buffer[roadId];
                    ColossalFramework.Math.Bezier3 curve = UnrestRoads.Curve(road);
                    points = new Vector3[33];
                    for (int k = 0; k < points.Length; k++) points[k] = curve.Position(k / 32f);
                }
                List<ushort> victims = new List<ushort>();
                Vector3 minimum=position, maximum=position;
                if(points!=null) foreach(Vector3 point in points) { minimum=Vector3.Min(minimum,point); maximum=Vector3.Max(maximum,point); }
                damageCandidates.Clear();
                List<ushort> localCandidates=new List<ushort>();
                Dictionary<ushort,bool> seen=new Dictionary<ushort,bool>();
                foreach(Vector3 person in crowd.DamagePositions) {
                    BuildingArea.Fill(buildings,Singleton<BuildingManager>.instance.m_buildingGrid,person-Vector3.one*64,person+Vector3.one*64,localCandidates);
                    foreach(ushort candidate in localCandidates) if(!seen.ContainsKey(candidate)) { seen.Add(candidate,true); damageCandidates.Add(candidate); }
                }
                Dictionary<ushort,Vector3[]> occupiedRoads=new Dictionary<ushort,Vector3[]>();
                // Keep deterministic candidate ordering before the existing random choice.
                damageCandidates.Sort();
                foreach (ushort i in damageCandidates)
                {
                    Building b = buildings[i];
                    if (!Healthy(b) || b.Info == null || !Zoned(b.Info.m_class.m_service) || b.m_parentBuilding != 0 || b.m_subBuilding != 0 ||
                        (b.m_flags & Building.Flags.Untouchable) != 0 || i == city.CityHallId || CivicSimulation.IsHall((ushort)i)) continue;
                    ushort access=b.m_accessSegment;
                    NetSegment[] segments=Singleton<NetManager>.instance.m_segments.m_buffer;
                    if(access==0 || access>=segments.Length || !UnrestRoads.Walkable(access)) continue;
                    NetSegment accessRoad=segments[access];
                    Vector3[] accessPoints;
                    if(!occupiedRoads.TryGetValue(access,out accessPoints)) {
                        int steps=Math.Max(8,Math.Min(128,(int)(accessRoad.m_averageLength/4)+1));
                        accessPoints=new Vector3[steps+1];
                        ColossalFramework.Math.Bezier3 accessCurve=UnrestRoads.Curve(accessRoad);
                        for(int k=0;k<=steps;k++) accessPoints[k]=accessCurve.Position(k/(float)steps);
                        occupiedRoads.Add(access,accessPoints);
                    }
                    if(!DevBuildingDamage.Near(b.m_position,crowd.DamagePositions,accessPoints,accessRoad.Info.m_halfWidth)) continue;
                    bool existing = false;
                    foreach (DamageRecord d in e.Damage) if (d.Building == i) existing = true;
                    if (!existing) victims.Add((ushort)i);
                }
                if (victims.Count > 0)
                {
                    ushort id = victims[Random.Next(victims.Count)];
                    Building b = buildings[id];
                    DamageRecord record = new DamageRecord { Building = id, BuildIndex = b.m_buildIndex,
                        X = b.m_position.x, Y = b.m_position.y, Z = b.m_position.z, Service = (int)b.Info.m_class.m_service,
                        Destroyed = kind == 2 };
                    DamageRecord[] previous = e.Damage;
                    List<DamageRecord> records = new List<DamageRecord>(previous); records.Add(record);
                    e.Damage = records.ToArray(); e.TotalDamage++; repairs = e.Damage.Length;
                    // Record before a native mutation so an exception cannot leave an untracked damaged site.
                    bool changed = record.Destroyed ? b.Info.m_buildingAI.CollapseBuilding(id, ref buildings[id], null, false, false, 0) :
                        b.Info.m_buildingAI.BurnBuilding(id, ref buildings[id], null, false);
                    if (changed)
                    {
                        if (CivicRecords.Draft != null) { if (kind == 2) CivicRecords.Draft.Collapses++; else CivicRecords.Draft.Fires++; }
                        if (e.TotalDamage == 1) CivicChirper.Post(e, "damage");
                        Debug.Log("[CivicPressure] Riot building damage: " + id + ", collapsed=" + record.Destroyed + ", road=" + roadId + ", side=" + side);
                    }
                    else { e.Damage = previous; e.TotalDamage--; repairs = previous.Length; }
                }
            }
        }

        private static void DamagePassedRoad(UnrestEvent e, ushort id, uint buildIndex)
        {
            if (!e.Riot || e.Police || e.RoadDamage >= 8 || !UnrestRules.DamageRoad(Random.NextDouble())) return;
            NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
            if (id == 0 || id >= roads.Length || !UnrestRoads.Walkable(id) || roads[id].m_buildIndex != buildIndex) return;
            if (roads[id].Info.m_netAI.CollapseSegment(id, ref roads[id], null, false))
            { e.RoadDamage++; Debug.Log("[CivicPressure] Riot passed road damaged: " + id); }
        }
        private static void CheckRepairs(UnrestEvent e)
        {
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            List<DamageRecord> pending = new List<DamageRecord>();
            foreach (DamageRecord d in e.Damage)
            {
                bool same = d.Building < buildings.Length && CivicSimulation.Live(buildings[d.Building]) && buildings[d.Building].m_buildIndex == d.BuildIndex;
                if (same && (buildings[d.Building].m_flags & (Building.Flags.Collapsed | Building.Flags.BurnedDown)) != 0) d.Destroyed = true;
                bool restored = same && Healthy(buildings[d.Building]);
                if (!same)
                {
                    // Demolishing an empty lot never counts. A completed replacement of the same service must occupy the site.
                    Vector3 site = new Vector3(d.X, d.Y, d.Z);
                    BuildingArea.Fill(buildings,Singleton<BuildingManager>.instance.m_buildingGrid,site-Vector3.one*8,site+Vector3.one*8,repairCandidates);
                    foreach (ushort i in repairCandidates) {
                        restored = Healthy(buildings[i]) && unchecked((int)(buildings[i].m_buildIndex - d.BuildIndex)) > 0 &&
                            buildings[i].Info != null && (int)buildings[i].Info.m_class.m_service == d.Service &&
                            (buildings[i].m_position - site).sqrMagnitude <= 8 * 8;
                        if(restored) break;
                    }
                }
                if (!restored) pending.Add(d);
            }
            e.Damage = pending.ToArray(); repairs = pending.Count;
        }
    }
}
