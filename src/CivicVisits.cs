using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    // In-person complaints (subscriber feedback 2): residents of a district with its own office, or of the city hall's
    // own district, travel there by the game's normal walking/transit routing. The complaint opens when they arrive,
    // or after VisitRules.TimeoutHours the usual way. Rarely a visitor causes a disturbance and one police car is called.
    // Runs on the simulation thread under CivicSimulation.Gate (from CivicComplaints.Tick). Nothing here is saved.
    internal static class CivicVisits
    {
        internal static void Rebase(long now, double factor)
        {
            foreach (VisitRecord v in visits.Values) v.Started = GameClock.Rebase(v.Started, now, factor);
            foreach (RoutineVisit v in routine.Values) v.Started = GameClock.Rebase(v.Started, now, factor);
            foreach (Disturbance d in disturbances.Values) {
                d.Started = GameClock.Rebase(d.Started, now, factor);
                d.PoliceArrived = GameClock.Rebase(d.PoliceArrived, now, factor);
                d.NextRequest = GameClock.Rebase(d.NextRequest, now, factor);
            }
            foreach (ushort key in new List<ushort>(lastTrouble.Keys)) lastTrouble[key] = GameClock.Rebase(lastTrouble[key], now, factor);
            lastRoutine = GameClock.Rebase(lastRoutine, now, factor);
        }
        private static readonly Dictionary<int, VisitRecord> visits = new Dictionary<int, VisitRecord>();
        private static readonly Dictionary<int, bool> arrived = new Dictionary<int, bool>();
        private static readonly Dictionary<ushort, Disturbance> disturbances = new Dictionary<ushort, Disturbance>();
        private static readonly Dictionary<ushort, long> lastTrouble = new Dictionary<ushort, long>();
        private static readonly System.Random random = new System.Random();

        internal static int Travelling { get { return visits.Count; } }
        // Developer tools (DEV build) and diagnostics: last visit event, a travelling visitor to follow, forced troublemaker.
        private static volatile string lastEvent = "";
        private static volatile int followCitizen;
        internal static volatile bool ForceTrouble;
        internal static string LastEvent { get { return lastEvent; } }
        internal static uint FollowCitizen { get { return (uint)followCitizen; } }
        internal static int Disturbances { get { return disturbances.Count; } }
        private static void Event(string text) { lastEvent = text; }

        internal static void Reset()
        {
            visits.Clear(); arrived.Clear(); lastTrouble.Clear(); marks = new VisitorMark[0];
            followCitizen = 0; ForceTrouble = false; lastEvent = "";
            routine.Clear(); routineBudget = 0; lastRoutine = 0; ForceRoutineKind = 0;
            foreach (Disturbance d in disturbances.Values) Calm(d);
            disturbances.Clear();
        }

        // Called when a complaint's intake is due. True: keep waiting for the visitor.
        internal static bool Hold(int key, ComplaintRecord c, CityView city, ComplaintHome home, long now)
        {
            try
            {
                VisitRecord visit;
                if (visits.TryGetValue(key, out visit))
                {
                    Citizen[] citizens = Singleton<CitizenManager>.instance.m_citizens.m_buffer;
                    Citizen person = citizens[visit.Citizen];
                    RegionView area = c.Region >= 0 && c.Region < city.Regions.Length ? city.Regions[c.Region] : null;
                    ushort serviceOffice = area != null && area.OfficeTransferred ? area.ServiceOfficeId : area != null ? area.OfficeId : (ushort)0;
                    Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                    if (serviceOffice != visit.Office || visit.Office >= buildings.Length || !CivicSimulation.Live(buildings[visit.Office]) ||
                        (buildings[visit.Office].m_flags & (Building.Flags.Collapsed | Building.Flags.BurnedDown)) != 0)
                    {
                        visits.Remove(key);
                        if (followCitizen == (int)visit.Citizen) followCitizen = 0;
                        Event("행정청 이용 불가로 방문 접수를 일반 접수로 전환");
                        return false;
                    }
                    bool visiting = (person.m_flags & Citizen.Flags.Created) != 0 && person.CurrentLocation == Citizen.Location.Visit && person.m_visitBuilding == visit.Office;
                    bool moving = MovingTo(person, visit.Office);
                    if (moving) visit.Near = Near(person, visit.Office);
                    VisitStatus status = VisitRules.Progress(moving, visiting, visit.Near, visit.Started, now);
                    if (status == VisitStatus.Travelling) { followCitizen = (int)visit.Citizen; return true; }
                    visits.Remove(key);
                    if (followCitizen == (int)visit.Citizen) followCitizen = 0;
                    Event(status == VisitStatus.Arrived ? "방문자 도착: 시민 #" + visit.Citizen + " → 건물 #" + visit.Office : "방문 실패(이동 취소 또는 시간 초과): 일반 접수로 전환");
                    if (status == VisitStatus.Arrived)
                    {
                        arrived[key] = true;
                        MaybeTrouble(visit, c.Region, now);
                    }
                    return false;
                }
                if (visits.Count >= VisitRules.MaxVisits) return false;
                ushort office;
                if (!InPerson(c.Region, city, home, out office)) { Event("방문 대상 아님(구청 없는 구·시청 먼 곳): 일반 접수"); return false; }
                uint citizen = Depart(home.Building, home.Citizen, office);
                // Nobody home in the representative household: another household of the same district goes instead.
                if (citizen == 0) citizen = DepartFromDistrict(c.Region, office, city);
                if (citizen == 0) { Event("출발할 수 있는 주민이 집에 없음: 일반 접수"); return false; }
                Event("방문자 출발: 시민 #" + citizen + " → 건물 #" + office);
                followCitizen = (int)citizen;
                visits[key] = new VisitRecord { Key = key, Citizen = citizen, Home = home.Building, Office = office, Started = now };
                return true;
            }
            catch (Exception error)
            {
                visits.Remove(key);
                Debug.LogWarning("[CivicPressure] In-person complaint skipped: " + error.Message);
                return false;
            }
        }

        // True once for a complaint that was just filed by a visitor (for the notice wording).
        internal static bool ConsumeArrival(int key)
        {
            if (!arrived.ContainsKey(key)) return false;
            arrived.Remove(key);
            return true;
        }

        // The problem went away while the visitor was travelling.
        internal static void Cancel(int key) { visits.Remove(key); arrived.Remove(key); }

        private static bool InPerson(int region, CityView city, ComplaintHome home, out ushort office)
        {
            office = 0;
            RegionView area = region >= 0 && region < city.Regions.Length ? city.Regions[region] : null;
            if (area == null || home == null || home.Building == 0) return false;
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            if (home.Building >= buildings.Length || !CivicSimulation.Live(buildings[home.Building]) || buildings[home.Building].m_buildIndex != home.BuildIndex) return false;
            ushort hall = city.CityHallId;
            bool officeDistrict = area.OfficeId != 0;
            if (area.OfficeTransferred && area.ServiceOfficeId != 0)
            {
                office = area.ServiceOfficeId;
                return office < buildings.Length && (buildings[office].m_flags & Building.Flags.Active) != 0 && office != home.Building;
            }
            bool hallDistrict = false;
            float distance = float.MaxValue;
            if (!officeDistrict && hall != 0 && hall < buildings.Length && CivicSimulation.Live(buildings[hall]))
            {
                hallDistrict = Singleton<DistrictManager>.instance.GetDistrict(buildings[hall].m_position) == region;
                distance = Vector3.Distance(buildings[hall].m_position, buildings[home.Building].m_position);
            }
            if (!VisitRules.InPerson(officeDistrict, hallDistrict, region == 0, distance)) return false;
            office = officeDistrict ? area.OfficeId : hall;
            return office != 0 && office < buildings.Length && (buildings[office].m_flags & Building.Flags.Active) != 0 && office != home.Building;
        }

        internal static bool CanVisit(int region, CityView city, ComplaintHome home)
        {
            ushort office;
            return InPerson(region, city, home, out office);
        }

        internal static bool Visiting(int key) { return visits.ContainsKey(key); }

        // Complaints waiting for their visitor, for the complaint list ("방문 접수 중").
        internal static List<VisitRecord> Pending() { return new List<VisitRecord>(visits.Values); }

        // A random residential building: its district's office, or the city hall when near enough.
        private static ushort RandomHome(Building[] buildings, CityView city, out ushort office, out int region) { return RandomHome(buildings, city, false, out office, out region); }

        private static ushort RandomHome(Building[] buildings, CityView city, bool hallErrand, out ushort office, out int region)
        {
            office = 0; region = 0;
            ushort id = (ushort)random.Next(1, buildings.Length);
            Building home = buildings[id];
            if (!CivicSimulation.Live(home) || home.Info == null || !(home.Info.m_buildingAI is ResidentialBuildingAI) || home.m_citizenUnits == 0) return 0;
            region = Singleton<DistrictManager>.instance.GetDistrict(home.m_position);
            RegionView area = region < city.Regions.Length ? city.Regions[region] : null;
            if (area == null) return 0;
            ushort hall = city.CityHallId;
            float distance = hall != 0 && hall < buildings.Length && CivicSimulation.Live(buildings[hall]) ? Vector3.Distance(buildings[hall].m_position, home.m_position) : float.MaxValue;
            ushort districtOffice = area.OfficeTransferred ? area.ServiceOfficeId : area.OfficeId;
            office = VisitRules.RoutineOffice(districtOffice, distance < float.MaxValue ? hall : (ushort)0, distance, hallErrand);
            if (office == 0 || office >= buildings.Length || (buildings[office].m_flags & Building.Flags.Active) == 0 || office == id) return 0;
            return id;
        }

        private static uint DepartFromDistrict(int region, ushort office, CityView city)
        {
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            for (int probe = 0; probe < 96; probe++)
            {
                ushort homeOffice; int homeRegion;
                ushort home = RandomHome(buildings, city, out homeOffice, out homeRegion);
                if (home == 0 || homeRegion != region || homeOffice != office) continue;
                uint citizen = Depart(home, 0, office);
                if (citizen != 0) return citizen;
            }
            return 0;
        }

        // Everyday paperwork visits. Budget grows with served residents during office hours (VisitRules).
        private static readonly Dictionary<uint, RoutineVisit> routine = new Dictionary<uint, RoutineVisit>();
        private static double routineBudget;
        private static long lastRoutine;
        // DEV buttons: 0 none, 1 any office, 2 city hall, 3 district office. Forced sends ignore office hours, the budget
        // and the per-office limit, and probe far more homes (user request 2026-09-18: "강제로 보내면 안 돼?").
        internal static volatile int ForceRoutineKind;
        internal static bool ForceRoutine { set { ForceRoutineKind = value ? 1 : 0; } }
        internal static int RoutineTravelling { get { return routine.Count; } }

        private static void Routine(CityView city, long now)
        {
            if (routine.Count > 0)
            {
                Citizen[] citizens = Singleton<CitizenManager>.instance.m_citizens.m_buffer;
                List<uint> done = new List<uint>();
                foreach (RoutineVisit v in routine.Values)
                {
                    Citizen person = citizens[v.Citizen];
                    bool visiting = (person.m_flags & Citizen.Flags.Created) != 0 && person.CurrentLocation == Citizen.Location.Visit && person.m_visitBuilding == v.Office;
                    bool moving = MovingTo(person, v.Office);
                    if (moving) v.Near = Near(person, v.Office);
                    VisitStatus status = VisitRules.Progress(moving, visiting, v.Near, v.Started, now);
                    if (status == VisitStatus.Travelling) continue;
                    done.Add(v.Citizen);
                    if (status == VisitStatus.Arrived) Event("일상 행정 방문 도착: 시민 #" + v.Citizen + " → 건물 #" + v.Office);
                }
                foreach (uint id in done) routine.Remove(id);
            }
            double hours = lastRoutine == 0 || now < lastRoutine ? 0 : (now - lastRoutine) / (double)GameClock.HourTicks;
            lastRoutine = now;
            int forcedKind = ForceRoutineKind;
            ForceRoutineKind = 0;
            bool forced = forcedKind != 0;
            if (!city.HasCityHall) { if (forced) Event("일상 행정 방문 실패: 시청이 지정되지 않았습니다."); return; }
            if (!forced)
            {
                if (!VisitRules.OfficeOpen(Singleton<SimulationManager>.instance.m_currentDayTimeHour)) return;
                int served = 0;
                foreach (RegionView area in city.Regions) if (area != null) served += area.Residents;
                routineBudget = VisitRules.RoutineBudget(routineBudget, served, hours);
            }
            for (int sent = 0; (forced ? sent < 1 : routineBudget >= 1 && routine.Count < VisitRules.MaxRoutine) && sent < 3; sent++)
            {
                if (!SendRoutine(city, now, forcedKind)) break;
                if (!forced) routineBudget -= 1;
            }
        }

        private static bool SendRoutine(CityView city, long now, int forcedKind)
        {
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            bool forced = forcedKind != 0;
            int probes = forced ? 600 : 48, rejectedOffice = 0, noHome = 0, wrongTarget = 0;
            for (int probe = 0; probe < probes; probe++)
            {
                ushort office; int region;
                bool hallErrand = forcedKind == 2 || (forcedKind != 3 && VisitRules.HallErrand(random.NextDouble()));
                ushort home = RandomHome(buildings, city, hallErrand, out office, out region);
                if (home == 0) { noHome++; continue; }
                // The DEV buttons ask for one destination in particular.
                if (forcedKind == 2 && office != city.CityHallId) { wrongTarget++; continue; }
                if (forcedKind == 3 && office == city.CityHallId) { wrongTarget++; continue; }
                int atOffice = 0;
                foreach (RoutineVisit v in routine.Values) if (v.Office == office) atOffice++;
                if (!forced && atOffice >= VisitRules.MaxRoutinePerOffice) { rejectedOffice++; continue; }
                uint citizen = Depart(home, 0, office);
                if (citizen == 0) continue;
                bool hall = office == city.CityHallId;
                routine[citizen] = new RoutineVisit { Citizen = citizen, Office = office, Started = now, Hall = hall, Purpose = VisitRules.Purpose(hall, random.NextDouble()) };
                followCitizen = (int)citizen;
                Event("일상 행정 방문 출발: 시민 #" + citizen + " → " + (hall ? "시청" : "구청") + " 건물 #" + office);
                return true;
            }
            if (forced) Event("일상 행정 방문 실패: 출발할 주민을 찾지 못했습니다 (집 후보 없음 " + noHome + "회, 목적지 불일치 " + wrongTarget + "회, 혼잡 " + rejectedOffice + "회). 구청이 지정된 구의 주택과 " + (forcedKind == 2 ? "시청 3km 안 주택" : "활성 행정청") + "을 확인하세요.");
            return false;
        }

        // Sends one grown-up at home from a household (the preferred resident first).
        private static uint Depart(ushort building, uint preferred, ushort office)
        {
            CitizenManager manager = Singleton<CitizenManager>.instance;
            Citizen[] citizens = manager.m_citizens.m_buffer;
            List<uint> candidates = new List<uint>();
            if (preferred != 0 && preferred < citizens.Length) candidates.Add(preferred);
            uint unit = Singleton<BuildingManager>.instance.m_buildings.m_buffer[building].m_citizenUnits;
            for (int guard = 0; unit != 0 && guard < 64; guard++)
            {
                CitizenUnit u = manager.m_units.m_buffer[unit];
                if ((u.m_flags & CitizenUnit.Flags.Home) != 0)
                    for (int i = 0; i < 5; i++) { uint id = u.GetCitizen(i); if (id != 0 && id != preferred) candidates.Add(id); }
                unit = u.m_nextUnit;
            }
            foreach (uint id in candidates)
            {
                ushort home = building;
                if (!Ready(citizens[id], home)) continue;
                CitizenInfo info = citizens[id].GetCitizenInfo(id);
                HumanAI ai = info == null ? null : info.m_citizenAI as HumanAI;
                if (ai == null || !ai.StartMoving(id, ref citizens[id], home, office)) continue;
                // Same order as the game's own trips: with the office as visit place the resident stays a while on arrival.
                citizens[id].SetVisitplace(id, office, 0u);
                return id;
            }
            return 0;
        }

        private static bool Ready(Citizen c, ushort home)
        {
            const Citizen.Flags unable = Citizen.Flags.Dead | Citizen.Flags.Sick | Citizen.Flags.Arrested | Citizen.Flags.Criminal |
                Citizen.Flags.Evacuating | Citizen.Flags.Collapsed | Citizen.Flags.Tourist | Citizen.Flags.MovingIn | Citizen.Flags.DummyTraffic;
            return (c.m_flags & Citizen.Flags.Created) != 0 && (c.m_flags & unable) == 0 && c.m_homeBuilding == home &&
                c.CurrentLocation == Citizen.Location.Home && c.m_instance == 0 && c.m_vehicle == 0 &&
                Citizen.GetAgeGroup(c.Age) >= Citizen.AgeGroup.Young;
        }

        private static bool Near(Citizen c, ushort office)
        {
            CitizenInstance instance = Singleton<CitizenManager>.instance.m_instances.m_buffer[c.m_instance];
            Vector3 target = Singleton<BuildingManager>.instance.m_buildings.m_buffer[office].m_position;
            return Vector3.Distance(instance.GetLastFramePosition(), target) <= VisitRules.ArrivalRadius;
        }

        private static bool MovingTo(Citizen c, ushort office)
        {
            if ((c.m_flags & Citizen.Flags.Created) == 0 || c.CurrentLocation != Citizen.Location.Moving || c.m_instance == 0) return false;
            CitizenInstance instance = Singleton<CitizenManager>.instance.m_instances.m_buffer[c.m_instance];
            return (instance.m_flags & CitizenInstance.Flags.Created) != 0 && instance.m_targetBuilding == office &&
                (instance.m_flags & CitizenInstance.Flags.TargetIsNode) == 0;
        }

        private static void MaybeTrouble(VisitRecord visit, int region, long now)
        {
            if (disturbances.ContainsKey(visit.Office)) return;
            long last;
            lastTrouble.TryGetValue(visit.Office, out last);
            bool forced = ForceTrouble;
            ForceTrouble = false;
            if (!forced && !Features.OfficeDisturbances) return;
            if (!forced && !VisitRules.Troublemaker(random.NextDouble(), now, last)) return;
            lastTrouble[visit.Office] = now;
            Disturbance d = new Disturbance { Office = visit.Office, Region = region, Citizen = visit.Citizen, Started = now };
            // A criminal at the building makes the game's police car come with sirens and send officers inside.
            Singleton<CitizenManager>.instance.m_citizens.m_buffer[visit.Citizen].Criminal = true;
            disturbances[visit.Office] = d;
            Event("난동 발생: 건물 #" + visit.Office);
            Notice(d, L.T("방문 민원인이 행정청에서 소란을 일으켜 경찰에 출동을 요청했습니다."));
        }

        // Once per complaint tick (about a second of real time).
        // Immutable copy for the UI thread (map pins). Rebuilt on every tick.
        private static volatile VisitorMark[] marks = new VisitorMark[0];
        internal static VisitorMark[] Marks { get { return marks; } }

        private static void PublishMarks()
        {
            List<VisitorMark> list = new List<VisitorMark>();
            foreach (VisitRecord v in visits.Values) if (v.Citizen != 0) list.Add(new VisitorMark { Citizen = v.Citizen, Office = v.Office, Kind = 0 });
            foreach (RoutineVisit v in routine.Values) if (v.Citizen != 0) list.Add(new VisitorMark { Citizen = v.Citizen, Office = v.Office, Kind = 1, Purpose = v.Purpose, Hall = v.Hall });
            foreach (Disturbance d in disturbances.Values) if (d.Citizen != 0) list.Add(new VisitorMark { Citizen = d.Citizen, Office = d.Office, Kind = 2 });
            marks = list.ToArray();
        }

        internal static void Tick(CityView city, long now)
        {
            try { Routine(city, now); }
            catch (Exception error) { Debug.LogWarning("[CivicPressure] Everyday office visits skipped: " + error.Message); }
            try { PublishMarks(); }
            catch (Exception error) { Debug.LogWarning("[CivicPressure] Visitor marks skipped: " + error.Message); }
            if (disturbances.Count == 0) return;
            List<ushort> finished = new List<ushort>();
            // Switched off in the options: a disturbance in progress ends quietly and the police car may leave.
            if (!Features.OfficeDisturbances) { foreach (Disturbance d in disturbances.Values) Calm(d); disturbances.Clear(); return; }
            foreach (Disturbance d in disturbances.Values)
            {
                try
                {
                    Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                    if (d.Office >= buildings.Length || !CivicSimulation.Live(buildings[d.Office])) { finished.Add(d.Office); continue; }
                    if (d.PoliceArrived == 0) WatchPolice(d, buildings, now);
                    switch (VisitRules.Status(d, now))
                    {
                        case DisturbanceStatus.Resolved:
                            Event("난동 종료: 경찰 정리 완료");
                            Notice(d, L.T("경찰이 소란 상황을 정리했습니다. 민원 업무를 정상 진행합니다."));
                            finished.Add(d.Office);
                            break;
                        case DisturbanceStatus.NoPolice:
                            Event("난동 종료: 경찰 미도착");
                            Notice(d, L.T("경찰이 도착하지 않은 채 소란 상황이 끝났습니다. 경찰서 배치와 도로 연결을 확인하시기 바랍니다."));
                            finished.Add(d.Office);
                            break;
                    }
                }
                catch (Exception error)
                {
                    Debug.LogWarning("[CivicPressure] Office disturbance ended early: " + error.Message);
                    finished.Add(d.Office);
                }
            }
            foreach (ushort office in finished)
            {
                Disturbance d;
                if (disturbances.TryGetValue(office, out d)) Calm(d);
                disturbances.Remove(office);
            }
        }

        private static void WatchPolice(Disturbance d, Building[] buildings, long now)
        {
            Vehicle[] cars = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            if (d.PoliceVehicle != 0)
            {
                Vehicle car = cars[d.PoliceVehicle];
                bool stillPolice = (car.m_flags & Vehicle.Flags.Created) != 0 && car.Info != null && car.Info.m_vehicleAI is PoliceCarAI;
                if (stillPolice && car.m_targetBuilding == d.Office) return;
                // The car dropped its target: it reached the office, or it was reassigned somewhere else.
                bool near = stillPolice && Vector3.Distance(car.GetLastFramePosition(), buildings[d.Office].m_position) < 120f;
                if (near || (stillPolice && (car.m_flags & Vehicle.Flags.Stopped) != 0))
                {
                    d.PoliceArrived = now;
                    Event("경찰 도착: 경찰차 #" + d.PoliceVehicle);
                    Notice(d, L.T("경찰이 도착해 소란 상황에 대응하고 있습니다."));
                    return;
                }
                d.PoliceVehicle = 0;
            }
            ushort coming = PoliceComing(d.Office, buildings, cars);
            if (coming != 0) { d.PoliceVehicle = coming; return; }
            if (!VisitRules.RequestPolice(d, false, now)) return;
            d.NextRequest = now + GameClock.Hours(VisitRules.RequestIntervalHours);
            Dispatch(d, buildings, cars);
        }

        // Same direct dispatch as riot police (CivicUnrest): the nearest open police station with a free car sends one
        // with sirens. A plain crime offer to the transfer manager was not answered in game (2026-09-17 report).
        private static void Dispatch(Disturbance d, Building[] buildings, Vehicle[] cars)
        {
            Vector3 position = buildings[d.Office].m_position;
            int[] used = new int[buildings.Length];
            bool[] existing = new bool[cars.Length];
            for (int i = 1; i < cars.Length; i++)
            {
                if ((cars[i].m_flags & Vehicle.Flags.Created) == 0 || cars[i].Info == null || !(cars[i].Info.m_vehicleAI is PoliceCarAI)) continue;
                existing[i] = true;
                if (cars[i].m_sourceBuilding < used.Length) used[cars[i].m_sourceBuilding]++;
            }
            ushort station = 0;
            float nearest = float.MaxValue;
            for (int i = 1; i < buildings.Length; i++)
            {
                Building b = buildings[i];
                if (!CivicSimulation.Live(b) || b.Info == null || b.m_fireIntensity != 0 ||
                    (b.m_flags & (Building.Flags.Active | Building.Flags.Evacuating | Building.Flags.Collapsed)) != Building.Flags.Active) continue;
                PoliceStationAI ai = b.Info.m_buildingAI as PoliceStationAI;
                if (ai == null || b.Info.m_class.m_level >= ItemClass.Level.Level4) continue;
                int budget = Singleton<EconomyManager>.instance.GetBudget(b.Info.m_class);
                int limit = (PlayerBuildingAI.GetProductionRate(b.m_productionRate, budget) * ai.PoliceCarCount + 99) / 100;
                float distance = (b.m_position - position).sqrMagnitude;
                if (used[i] < limit && distance < nearest) { station = (ushort)i; nearest = distance; }
            }
            if (station == 0) { Event("경찰 출동 불가: 가용 차량이 있는 운영 중 경찰서 없음"); return; }
            TransferManager.TransferOffer offer = new TransferManager.TransferOffer
            {
                Building = d.Office, Position = position, Priority = 7, Amount = 1, Active = false
            };
            buildings[station].Info.m_buildingAI.StartTransfer(station, ref buildings[station], TransferManager.TransferReason.Crime, offer);
            for (int i = 1; i < cars.Length; i++)
            {
                if (existing[i] || (cars[i].m_flags & Vehicle.Flags.Created) == 0 || cars[i].m_sourceBuilding != station ||
                    cars[i].m_targetBuilding != d.Office || cars[i].Info == null || !(cars[i].Info.m_vehicleAI is PoliceCarAI)) continue;
                // Sirens on arrival make officers step out and the car stop at the office (PoliceCarAI.ArriveAtTarget).
                cars[i].m_flags |= Vehicle.Flags.Emergency2;
                d.PoliceVehicle = (ushort)i;
                Event("경찰 출동: 경찰서 #" + station + " → 경찰차 #" + i);
                Debug.Log("[CivicPressure] Office disturbance: police car " + i + " from station " + station + " to building " + d.Office);
                return;
            }
            Event("경찰 출동 실패: 경찰차 생성 또는 경로 설정 실패");
        }

        // Harmony (UnrestPatches.KeepPoliceOnScene): the responding car stays at the office until the disturbance ends.
        internal static bool Holding(ushort vehicle)
        {
            if (vehicle == 0 || disturbances.Count == 0) return false;
            foreach (Disturbance d in disturbances.Values)
                if (d.PoliceVehicle == vehicle && d.PoliceArrived > 0) return true;
            return false;
        }

        private static ushort PoliceComing(ushort office, Building[] buildings, Vehicle[] cars)
        {
            ushort id = buildings[office].m_guestVehicles;
            for (int guard = 0; id != 0 && guard < 256; guard++)
            {
                Vehicle car = cars[id];
                if ((car.m_flags & Vehicle.Flags.Created) != 0 && car.m_targetBuilding == office && car.Info != null && car.Info.m_vehicleAI is PoliceCarAI &&
                    (TransferManager.TransferReason)car.m_transferType == TransferManager.TransferReason.Crime)
                    return id;
                id = car.m_nextGuestVehicle;
            }
            return 0;
        }

        // Clears the flag this mod set, unless the police already took the visitor away.
        private static void Calm(Disturbance d)
        {
            try
            {
                Citizen[] citizens = Singleton<CitizenManager>.instance.m_citizens.m_buffer;
                if (d.Citizen != 0 && d.Citizen < citizens.Length && (citizens[d.Citizen].m_flags & Citizen.Flags.Created) != 0 && !citizens[d.Citizen].Arrested)
                    citizens[d.Citizen].Criminal = false;
            }
            catch (Exception error) { Debug.LogWarning("[CivicPressure] Disturbance cleanup: " + error.Message); }
        }

        private static void Notice(Disturbance d, string text)
        {
            CityView city = CivicSimulation.View;
            string office = Singleton<BuildingManager>.instance.GetBuildingName(d.Office, default(InstanceID));
            CivicChirper.PostComplaint((string.IsNullOrEmpty(office) ? L.T("행정청") : office) + " · " + text + L.T(" #방문민원"), city, d.Region);
        }
    }
}
