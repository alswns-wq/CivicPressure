using System;
using System.Collections.Generic;
using ColossalFramework;
using ICities;
using UnityEngine;

namespace CivicPressure
{
    internal sealed class RegionView
    {
        internal string Name;
        internal int Residents;
        internal int Housed, Unlocated;
        internal int[] Affected;
        internal int[] Severe;
        internal int Buildings, Abandoned, WarningBuildings, SevereBuildings;
        internal double Score;
        internal string Office;
        internal ushort OfficeId;
        internal string ServiceOffice;
        internal ushort ServiceOfficeId;
        internal string TransferStage;
        internal bool OfficeTransferred;
        internal int Trust = 50;
    }

    internal sealed class CityView
    {
        internal int Generation;
        internal ushort Selected;
        internal uint SelectedBuildIndex;
        internal byte SelectedDistrict;
        internal string SelectedName = L.T("건물을 선택하세요.");
        internal bool Registered;
        internal string CityName = L.T("우리 도시");
        internal string HallTitle = "";
        internal bool AdministrationComplete;
        internal int RequiredOffices, AssignedOffices;
        internal bool CanAssign;
        internal string CityHall = L.T("미지정");
        internal ushort CityHallId;
        internal bool HasCityHall;
        internal bool CityHallOperating;
        internal RegionView[] Regions = new RegionView[256];
        internal RegionView All;
        internal double CityScore = double.NaN;
        internal int AveragedDistricts;
        internal bool Scanning;
        internal int ScanProgress;
        internal DateTime CensusUpdatedUtc;
        internal string Status = L.T("시민 집계를 준비하고 있습니다.");
        internal ComplaintView[] Complaints = new ComplaintView[0];
        internal ComplaintMarker[] Markers = new ComplaintMarker[0];
    }

    internal static class CivicSimulation
    {
        private struct Request
        {
            internal CityView Selection;
            internal int Action; // 0 city hall, 1 district office, 2 clear selected designation.
        }

        // Only this gate crosses UI, save callbacks and simulation callbacks. Game reads occur in Tick.
        private static readonly object Gate = new object();
        private static readonly Queue<Request> Requests = new Queue<Request>();
        private static HallRecord[] halls = new HallRecord[256];
        // Publish only after the new snapshot and its owned arrays are fully populated.
        private static volatile CityView view = new CityView();
        private static bool transferBaselineReady;
        private static CityCensus scan;
        private static CityCensus completed;
        private static CensusProjection projection;
        private static long nextScan;
        private static bool refreshRequested;
        private static DateTime censusUpdatedUtc;
        private static int censusCount;
        private static bool scanWasManual;
        private static ushort selected;
        private static int generation;
        private static int ticks;
        private static bool active;
        private static bool citySession;
        private static bool dirty;
        private static long nextComplaints;
        private static int promiseKey = -1;
        private static long decisionStamp;
        private static bool decisionAccept, decisionAgree;
        internal static void Decide(int key, long stamp, bool accept, int session) { Decide(key, stamp, accept, false, session); }
        // agree: conditional agreement for facility opposition (third button).
        internal static void Decide(int key, long stamp, bool accept, bool agree, int session)
        {
            lock (Gate)
            {
                if (!active || session != generation || promiseKey >= 0) return;
                promiseKey = key; decisionStamp = stamp; decisionAccept = accept; decisionAgree = agree;
            }
        }
        internal static void LoadComplaints(byte[] bytes) { lock (Gate) CivicComplaints.Load(bytes); }
        internal static byte[] SaveComplaints() { lock (Gate) return CivicComplaints.Save(); }
        internal static void LoadRecords(byte[] bytes) { lock (Gate) CivicRecords.Load(bytes); }
        internal static void LoadHistory(byte[] bytes) { lock (Gate) ComplaintHistory.Load(bytes); }
        internal static void LoadTrend(byte[] bytes) { lock(Gate) HappinessTrend.Load(bytes); }
        internal static void LoadRecurrence(byte[] bytes) { lock(Gate) { ComplaintRecurrence.Load(bytes); if(bytes==null || bytes.Length==0) ComplaintHistory.SeedRecurrence(); } }
        internal static byte[] SaveRecurrence() { lock(Gate) return ComplaintRecurrence.Save(); }
        internal static void LoadStanding(byte[] bytes) { lock(Gate) CivicStanding.Load(bytes); }
        internal static byte[] SaveStanding() { lock(Gate) return CivicStanding.Save(); }
        internal static StandingView Standing() { lock(Gate) return CivicStanding.Snapshot(Singleton<SimulationManager>.instance.m_currentGameTime.Ticks); }
        internal static byte[] SaveTrend() { lock(Gate) return HappinessTrend.Save(); }
        internal static long TrendRevision() {lock(Gate)return HappinessTrend.Revision;}
        internal static long ReportsRevision(bool history) {lock(Gate)return history ? ComplaintHistory.Revision : CivicRecords.ReportRevision;}
        internal static TrendPoint[] Trend(int region) { lock(Gate) return HappinessTrend.Points(region); }
        internal static byte[] SaveHistory() { lock (Gate) return ComplaintHistory.Save(); }
        internal static string[] History(int region) { lock (Gate) return ComplaintHistory.Pages(region); }
        internal static byte[] SaveRecords() { lock (Gate) return CivicRecords.Save(); }
        internal static string[] Demands() { lock (Gate) return ProtestDemands.Pages(CivicRecords.Draft, view, CivicUnrest.View); }
        internal static string[] Reports() { lock (Gate) return CivicRecords.ReportTexts(); }
        private static byte[] preservedData;
        private static string status = L.T("시청을 지정하세요. 시민 집계에는 잠시 시간이 필요합니다.");

        internal static CityView View { get { return view; } }

        // Called only from the simulation step while Gate is held.
        internal static bool IsHall(ushort id)
        {
            foreach (HallRecord hall in halls) if (hall.Building == id) return true;
            return false;
        }

        internal static void PrepareLoad()
        {
            Stop();
        }

        internal static void Load(byte[] data)
        {
            lock (Gate)
            {
                try
                {
                    halls = HallSave.Decode(data);
                    preservedData = null;
                    status = data == null || data.Length == 0 ? L.T("시청을 지정하세요. 시민 집계에는 잠시 시간이 필요합니다.") :
                        L.T("저장된 행정 건물을 복원했습니다. 현재 도시와 대조하여 집계합니다.");
                }
                catch (Exception error)
                {
                    preservedData = data;
                    halls = new HallRecord[256];
                    status = L.T("저장 데이터 해석 실패: 원본 보존 중. 건물 지정이 잠겼습니다.");
                    Debug.LogError("[CivicPressure] Administration data rejected; original bytes will be preserved. " + error);
                }
            }
        }

        internal static byte[] Save()
        {
            lock (Gate) return citySession ? (preservedData ?? HallSave.Encode(halls)) : null;
        }

        internal static void Start()
        {
            lock (Gate)
            {
                generation++;
                active = true;
                citySession = true;
                dirty = true;
                scan = null;
                completed = null; projection = null;
                nextScan = 0; refreshRequested = false; censusUpdatedUtc = DateTime.MinValue; censusCount = 0; scanWasManual = false;
                selected = 0;
                transferBaselineReady = false;
                Requests.Clear();
            }
        }

        internal static void Stop()
        {
            lock (Gate)
            {
                active = false;
                GameClock.Reset();
                citySession = false;
                CivicUnrest.Reset();
                CivicComplaints.Reset(); nextComplaints = 0; promiseKey = -1;
                CivicRecords.Reset(); ComplaintHistory.Reset(); HappinessTrend.Reset(); ComplaintRecurrence.Reset();
                generation++;
                halls = new HallRecord[256];
                view = new CityView();
                transferBaselineReady = false;
                selected = 0;
                preservedData = null;
                scan = null;
                completed = null; projection = null;
                nextScan = 0; refreshRequested = false; censusUpdatedUtc = DateTime.MinValue; scanWasManual = false;
                Requests.Clear();
                status = L.T("시청을 지정하세요. 시민 집계에는 잠시 시간이 필요합니다.");
            }
        }

        internal static void Select(ushort building)
        {
            lock (Gate) { if (selected != building) { selected = building; dirty = true; } }
        }

        internal static void Enqueue(CityView selection, int action)
        {
            lock (Gate)
            {
                if (!active || selection.Generation != generation || Requests.Count >= 16) return;
                Requests.Enqueue(new Request { Selection = selection, Action = action });
            }
        }

        internal static void Refresh()
        {
            lock (Gate) { refreshRequested = true; dirty = true; }
        }

        internal static bool Live(Building building)
        {
            return (building.m_flags & Building.Flags.Created) != 0 &&
                (building.m_flags & (Building.Flags.Deleted | Building.Flags.Demolishing)) == 0;
        }

        internal static bool Assignable(Building building)
        {
            return Live(building) && (building.m_flags & (Building.Flags.Collapsed | Building.Flags.BurnedDown)) == 0;
        }

        private static bool Matches(HallRecord hall, Building[] buildings)
        {
            return hall.Building != 0 && hall.Building < buildings.Length && Live(buildings[hall.Building]) &&
                buildings[hall.Building].m_buildIndex == hall.BuildIndex;
        }

        internal static void Tick()
        {
            lock (Gate)
            {
                if (!active) return;
                try
                {
                    Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                    DistrictManager districts = Singleton<DistrictManager>.instance;
                    ticks++;
                    if (ticks % 60 == 0 || dirty || Requests.Count > 0)
                    {
                        ValidateHalls(buildings, districts);
                        while (Requests.Count > 0) Apply(Requests.Dequeue(), buildings, districts);
                        dirty = true;
                    }
                    SimulationManager simulation = Singleton<SimulationManager>.instance;
                    // Mod hours follow the day/night cycle, so complaints do not rush by without Real Time (GameClock).
                    DateTime now = simulation.m_currentGameTime;
                    if (!GameClock.Available) return;
                    double clockFactor = GameClock.Synchronize(simulation.m_timePerFrame.Ticks, SimulationManager.DAYTIME_FRAMES);
                    if (clockFactor != 1) {
                        CivicComplaints.Rebase(now.Ticks, clockFactor);
                        CivicRecords.Rebase(now.Ticks, clockFactor);
                        ComplaintRecurrence.Rebase(now.Ticks, clockFactor);
                        CivicStanding.Rebase(now.Ticks, clockFactor);
                        CivicUnrest.Rebase(now.Ticks, clockFactor);
                        CivicVisits.Rebase(now.Ticks, clockFactor);
                        HappinessTrend.ClockChanged(); // Old samples have a different hour length; preserve unreadable data.
                        promiseKey = -1; dirty = true;
                    }
                    long clock = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (refreshRequested) { scan = null; nextScan = 0; refreshRequested = false; scanWasManual = true; }
                    if (scan == null && clock >= nextScan)
                    {
                        scan = new CityCensus(buildings.Length);
                        dirty = true;
                    }
                    if (scan != null && scan.Step())
                    {
                        completed = scan;
                        CivicComplaints.Census(completed);
                        scan = null;
                        nextScan = clock + 10 * System.Diagnostics.Stopwatch.Frequency;
                        censusUpdatedUtc = DateTime.UtcNow;
                        censusCount++;
                        if (censusCount <= 3 || scanWasManual)
                        {
                            int residents = 0, warning = 0, severe = 0, abandoned = 0;
                            for (int i = 0; i < 256; i++) { residents += completed.Residents[i]; warning += completed.Warning[i]; severe += completed.Major[i]; abandoned += completed.Abandoned[i]; }
                            Debug.Log("[CivicPressure] Census complete: residents=" + residents + ", created=" + completed.Created + ", noHome=" + completed.NoHome +
                                ", warningBuildings=" + warning + ", severeBuildings=" + severe + ", abandoned=" + abandoned);
                        }
                        scanWasManual = false;
                        dirty = true;
                    }
                    if (scan != null && ticks % 8 == 0) dirty = true;
                    if (dirty) { Publish(buildings, districts); dirty = false; }
                    if (promiseKey >= 0) { CivicComplaints.Decide(promiseKey, decisionStamp, decisionAccept, decisionAgree, view, now); promiseKey = -1; nextComplaints = 0; }
                    if (clock >= nextComplaints)
                    {
                        CivicComplaints.Tick(view, now);
                        Publish(buildings, districts);
                        nextComplaints = clock + System.Diagnostics.Stopwatch.Frequency;
                    }
                    CivicUnrest.Tick(view, now);
                }
                catch (Exception error)
                {
                    active = false;
                    CivicUnrest.Enable(false);
                    Requests.Clear();
                    view = new CityView { Status = L.T("행복도 모듈 오류로 작업을 중단했습니다. output_log.txt를 확인하세요.") };
                    Debug.LogError("[CivicPressure] Simulation stopped after an error. " + error);
                }
            }
        }

        private static void ValidateHalls(Building[] buildings, DistrictManager districts)
        {
            for (int i = 0; i < halls.Length; i++)
            {
                HallRecord hall = halls[i];
                if (hall.Building == 0) continue;
                bool valid = Matches(hall, buildings);
                if (valid && i > 0)
                    valid = i < districts.m_districts.m_buffer.Length &&
                        (districts.m_districts.m_buffer[i].m_flags & District.Flags.Created) != 0 &&
                        districts.GetDistrict(buildings[hall.Building].m_position) == i;
                if (valid) continue;
                halls[i] = new HallRecord();
                status = L.T("철거·건물 교체·지구 변경으로 행정 건물 지정을 해제했습니다.");
                Debug.Log("[CivicPressure] Invalid hall designation cleared: district=" + i + ", building=" + hall.Building);
            }
        }

        private static void Apply(Request request, Building[] buildings, DistrictManager districts)
        {
            CityView selection = request.Selection;
            ushort id = selection.Selected;
            if (selection.Generation != generation || preservedData != null || id == 0 || id >= buildings.Length ||
                !Live(buildings[id]) || buildings[id].m_buildIndex != selection.SelectedBuildIndex)
            { status = L.T("선택한 건물이 변경되었습니다. 다시 선택하세요."); return; }
            byte district = districts.GetDistrict(buildings[id].m_position);
            if (request.Action != 2 && !Assignable(buildings[id]))
            { status = L.T("붕괴·전소 건물은 지정할 수 없습니다."); return; }
            if (request.Action == 1 && (halls[0].Building == 0 || district == 0 || district >= districts.m_districts.m_buffer.Length ||
                (districts.m_districts.m_buffer[district].m_flags & District.Flags.Created) == 0 || id == halls[0].Building))
            { status = L.T("시청 지정 후, 일반 지구 안의 다른 건물을 구청으로 선택하세요."); return; }
            if (request.Action == 1 && district != selection.SelectedDistrict)
            { status = L.T("선택 후 지구 경계가 바뀌었습니다. 다시 선택하세요."); return; }
            for (int i = 0; i < halls.Length; i++)
                if (halls[i].Building == id) halls[i] = new HallRecord();
            if (request.Action != 2)
            {
                byte target = request.Action == 0 ? (byte)0 : district;
                halls[target] = new HallRecord { District = target, Building = id, BuildIndex = buildings[id].m_buildIndex };
            }
            status = request.Action == 0 ? L.T("시청 지정 완료. 다른 구청 지정은 유지됩니다.") :
                request.Action == 1 ? L.T("구청 지정 완료.") : L.T("선택한 건물의 지정을 해제했습니다.");
            Debug.Log("[CivicPressure] Administration changed: action=" + request.Action + ", building=" + id + ", district=" + district);
        }

        private static string BuildingName(ushort id)
        {
            return Singleton<BuildingManager>.instance.GetBuildingName(id, default(InstanceID));
        }

        private static ushort NearestOperatingOffice(int excludedDistrict, Vector3 position, Building[] buildings, DistrictManager districts, out int ownerDistrict)
        {
            ushort nearest = 0;
            float best = float.MaxValue;
            ownerDistrict = -1;
            for (int i = 0; i < halls.Length; i++)
            {
                HallRecord candidate = halls[i];
                if (i == excludedDistrict || candidate.Building == 0 || !Matches(candidate, buildings) ||
                    !Assignable(buildings[candidate.Building]) || (buildings[candidate.Building].m_flags & Building.Flags.Active) == 0) continue;
                if (i > 0 && (i >= districts.m_districts.m_buffer.Length ||
                    (districts.m_districts.m_buffer[i].m_flags & District.Flags.Created) == 0 ||
                    districts.GetDistrict(buildings[candidate.Building].m_position) != i)) continue;
                Vector3 delta = buildings[candidate.Building].m_position - position;
                float distance = delta.x * delta.x + delta.z * delta.z;
                if (distance >= best) continue;
                best = distance;
                nearest = candidate.Building;
                ownerDistrict = i;
            }
            return nearest;
        }

        private static void Publish(Building[] buildings, DistrictManager districts)
        {
            CityView next = new CityView();
            next.Generation = generation;
            next.Status = status;
            next.Scanning = scan != null;
            next.ScanProgress = scan == null ? 100 : scan.Progress;
            next.CensusUpdatedUtc = censusUpdatedUtc;
            next.HasCityHall = halls[0].Building != 0;
            next.CityHallId = halls[0].Building;
            next.CityHallOperating = next.HasCityHall && Matches(halls[0], buildings) && Assignable(buildings[halls[0].Building]);
            SimulationMetaData metadata = Singleton<SimulationManager>.instance.m_metaData;
            if (metadata != null && !string.IsNullOrEmpty(metadata.m_CityName)) next.CityName = metadata.m_CityName;
            for (int i = 1; i < districts.m_districts.m_buffer.Length && i < halls.Length; i++)
            {
                if ((districts.m_districts.m_buffer[i].m_flags & District.Flags.Created) == 0) continue;
                next.RequiredOffices++;
                if (halls[i].Building != 0) next.AssignedOffices++;
            }
            next.AdministrationComplete = next.HasCityHall && next.RequiredOffices == next.AssignedOffices;
            if (next.HasCityHall) next.CityHall = BuildingName(halls[0].Building);
            if (selected != 0 && selected < buildings.Length && Live(buildings[selected]))
            {
                next.Selected = selected;
                next.SelectedBuildIndex = buildings[selected].m_buildIndex;
                next.SelectedDistrict = districts.GetDistrict(buildings[selected].m_position);
                next.SelectedName = BuildingName(selected);
                next.CanAssign = preservedData == null && Assignable(buildings[selected]);
                for (int i = 0; i < halls.Length; i++) if (halls[i].Building == selected)
                {
                    next.Registered = true;
                    next.HallTitle = i == 0 ? AdministrationTitle.City(next.CityName) : districts.GetDistrictName((byte)i) + L.T(" 구청");
                }
            }
            if (completed != null)
            {
                if(projection==null || !projection.Matches(completed))projection=new CensusProjection(completed);
                RegionView total = projection.Total;
                List<double> scores = new List<double>();
                for (int i = 0; i < next.Regions.Length; i++)
                {
                    int residents = completed.Residents[i];
                    
                    
                    
                    
                    if (i > 0 && (i >= districts.m_districts.m_buffer.Length ||
                        (districts.m_districts.m_buffer[i].m_flags & District.Flags.Created) == 0)) continue;
                    RegionView region = new RegionView {
                        Name = i == 0 ? L.T("일반 지구 밖") : districts.GetDistrictName(i),
                        Residents = residents,
                        Housed = completed.Housed[i],
                        Affected = projection.Affected[i],
                        Severe = projection.Severe[i],
                        Buildings = completed.Buildings[i], Abandoned = completed.Abandoned[i],
                        WarningBuildings = completed.Warning[i], SevereBuildings = completed.Major[i],
                        // Trust bonus and opposition penalties (CivicStanding) on top of the measured score.
                        Score = double.IsNaN(projection.Scores[i]) ? projection.Scores[i] : Math.Max(0, Math.Min(100, projection.Scores[i] + CivicStanding.Adjustment(i, i > 0 && halls[i].Building != 0, Singleton<SimulationManager>.instance.m_currentGameTime.Ticks))),
                        OfficeId = i == 0 ? (ushort)0 : halls[i].Building,
                        Trust = CivicRecords.Trust(i),
                        Office = i == 0 ? L.T("구청 지정 대상 아님") : halls[i].Building == 0 ? L.T("미지정") : BuildingName(halls[i].Building)
                    };
                    if (i > 0 && halls[i].Building != 0 && Matches(halls[i], buildings))
                    {
                        if (Assignable(buildings[halls[i].Building]))
                        {
                            region.ServiceOfficeId = halls[i].Building;
                            region.ServiceOffice = region.Office;
                        }
                        else
                        {
                            int owner;
                            ushort delegated = NearestOperatingOffice(i, buildings[halls[i].Building].m_position, buildings, districts, out owner);
                            region.OfficeTransferred = true;
                            region.ServiceOfficeId = delegated;
                            region.ServiceOffice = delegated == 0 ? L.T("미지정") : BuildingName(delegated);
                            region.TransferStage = delegated == 0 ? L.T("대체 행정청 없음") : owner == 0 ? L.T("시청 이관 담당") : districts.GetDistrictName((byte)owner) + L.T(" 구청 이관 담당");
                        }
                    }
                    next.Regions[i] = region;
                    if (i > 0 && !double.IsNaN(region.Score)) scores.Add(region.Score);
                }
                
                next.All = total;
                next.AveragedDistricts = scores.Count;
                next.CityScore = Happiness.CityScore(scores.ToArray(), total.Score);
            }
            next.Complaints = CivicComplaints.Snapshot(next, Singleton<SimulationManager>.instance.m_currentGameTime);
            next.Markers = CivicComplaints.Markers(next);
            if (CivicComplaints.Error.Length > 0) next.Status = CivicComplaints.Error;
            if (CivicRecords.Error.Length > 0) next.Status = CivicRecords.Error;
            if (ComplaintRecurrence.Error.Length > 0) next.Status = ComplaintRecurrence.Error;
            HappinessTrend.Capture(next, Singleton<SimulationManager>.instance.m_currentGameTime.Ticks);
            if (completed != null)
            {
                if (transferBaselineReady)
                {
                    if (view.CityHallId == next.CityHallId && view.CityHallOperating && !next.CityHallOperating)
                        CivicChirper.PostText(CivicChirper.CityHallClosureText(next), CivicChirper.CitySender(next));
                    for (int i = 1; i < next.Regions.Length; i++)
                    {
                        RegionView current = next.Regions[i];
                        RegionView previous = view.Regions[i];
                        if (!NewTransfer(previous, current)) continue;
                        string notice = CivicChirper.TransferText(current);
                        if (current.ServiceOfficeId != 0) CivicChirper.PostComplaint(notice, next, i);
                        else CivicChirper.PostText(notice, CivicChirper.CitySender(next));
                    }
                }
                transferBaselineReady = true;
            }
            view = next;
        }

        internal static bool NewTransfer(RegionView previous, RegionView current)
        {
            return previous != null && current != null && current.OfficeTransferred && current.OfficeId != 0 &&
                previous.OfficeId == current.OfficeId &&
                (!previous.OfficeTransferred || previous.ServiceOfficeId != current.ServiceOfficeId);
        }
    }

    public sealed class Simulation : ThreadingExtensionBase
    {
        public override void OnAfterSimulationTick()
        {
            CivicSimulation.Tick();
            DevVehicleAssault.Tick(); DevCasualties.Tick();
        }
    }

    public sealed class SaveData : SerializableDataExtensionBase
    {
        public override void OnCreated(ISerializableData data)
        {
            base.OnCreated(data);
            CivicSimulation.PrepareLoad();
        }

        public override void OnLoadData()
        {
            GameClock.Load(serializableDataManager.LoadData(GameClock.Key));
            if (!GameClock.Available) Debug.LogError("[CivicPressure] Clock data unreadable; simulation paused and original clock bytes preserved.");
            CivicSimulation.Load(serializableDataManager.LoadData(HallSave.Key));
            CivicUnrest.Load(serializableDataManager.LoadData(UnrestSave.Key));
            CivicSimulation.LoadComplaints(serializableDataManager.LoadData(ComplaintSave.Key));
            CivicSimulation.LoadRecords(serializableDataManager.LoadData(CivicRecords.Key));
            CivicSimulation.LoadHistory(serializableDataManager.LoadData(ComplaintHistory.Key));
            CivicSimulation.LoadTrend(serializableDataManager.LoadData(HappinessTrend.Key));
            CivicSimulation.LoadRecurrence(serializableDataManager.LoadData(ComplaintRecurrence.Key));
            CivicSimulation.LoadStanding(serializableDataManager.LoadData(StandingSave.Key));
        }

        public override void OnSaveData()
        {
            byte[] data = CivicSimulation.Save();
            if (data == null) return;
            byte[] clockData = GameClock.Save();
            if (clockData != null) serializableDataManager.SaveData(GameClock.Key, clockData);
            serializableDataManager.SaveData(HallSave.Key, data);
            serializableDataManager.SaveData(UnrestSave.Key, CivicUnrest.Save());
            serializableDataManager.SaveData(ComplaintSave.Key, CivicSimulation.SaveComplaints());
            serializableDataManager.SaveData(CivicRecords.Key, CivicSimulation.SaveRecords());
            serializableDataManager.SaveData(ComplaintHistory.Key, CivicSimulation.SaveHistory());
            serializableDataManager.SaveData(HappinessTrend.Key, CivicSimulation.SaveTrend());
            serializableDataManager.SaveData(ComplaintRecurrence.Key, CivicSimulation.SaveRecurrence());
            serializableDataManager.SaveData(StandingSave.Key, CivicSimulation.SaveStanding());
            Debug.Log("[CivicPressure] Administration data saved.");
        }
    }
}
