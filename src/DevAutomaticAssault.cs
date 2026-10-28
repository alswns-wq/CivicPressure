using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    internal static class DevAssaultSchedulingRules
    {
        internal static bool RoadAvailable(int count) { return count>=0 && count<3; }
        internal static bool Retire(uint frame,uint due,uint next) { return (int)(frame-due)>=0 && (int)(frame-next)>=0; }
    }
    internal sealed partial class DevVehicleAssault
    {
        private sealed class Wreck
        {
            internal ushort Info;
            internal uint Units;
            internal long Riot;
            internal uint RetireAt;
            internal Vehicle.Frame Original;
            internal Vehicle.Flags Flags;
            internal DevAssaultView View;
        }
        private static readonly Dictionary<ushort, Wreck> wrecks = new Dictionary<ushort, Wreck>();
        private static bool[] wreckIds;
        private static uint autoScanFrame, nextRetirement;
        // Published scan result, read by the visual thread without another vehicle-grid search.
        private static volatile DevCrowdPresence emptyAssaultArea;
        internal static bool NoTargets(long stamp)
        { DevCrowdPresence area=emptyAssaultArea; return stamp!=0 && area!=null && area.Stamp==stamp; }
        private static volatile DevAssaultView[] wreckViews = new DevAssaultView[0];
        internal static DevAssaultView[] WreckViews { get { return wreckViews; } }
        // Simulation thread only. Never burn a police vehicle carrying a native officer or prisoner.
        internal static bool DevBurnEmptyPolice(ushort id,long stamp)
        {
            lock(Gate) {
                Vehicle[] cars=Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
                Vehicle car=cars[id]; CitizenManager citizens=Singleton<CitizenManager>.instance;
                if(!ready || visualFailed || car.Info==null || !(car.Info.m_vehicleAI is PoliceCarAI) ||
                    (car.m_flags & (Vehicle.Flags.Created|Vehicle.Flags.Spawned|Vehicle.Flags.Deleted))!=(Vehicle.Flags.Created|Vehicle.Flags.Spawned)) return false;
                uint[] occupants=DevVehicleAssaultRules.Manifest(id,car.m_citizenUnits,citizens.m_units.m_buffer,citizens.m_citizens.m_buffer,citizens.m_instances.m_buffer);
                if(occupants==null || occupants.Length!=0) return false;
                Vehicle.Frame original=car.GetLastFrameData();
                DevAssaultView view=new DevAssaultView { Vehicle=id, Position=original.m_position, Seconds=18, Riot=stamp };
                if(wreckIds==null) wreckIds=new bool[cars.Length];
                wrecks[id]=new Wreck { Info=car.m_infoIndex,Units=car.m_citizenUnits,Riot=stamp,Original=original,Flags=car.m_flags,View=view };
                PoliceDeployment.Released(id);
                PoliceDeployment.Freeze(ref cars[id]);
                cars[id].m_flags|=Vehicle.Flags.Congestion;
                cars[id].m_flags&=~Vehicle.Flags.Emergency2;
                wreckIds[id]=true; PublishWrecks(); return true;
            }
        }
        private void AutoBegin(UnrestView riot, long stamp)
        {
            if (visualFailed) return;
            uint frame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
            if (!DevVehicleAssaultRules.AutoAllowed(stamp != 0, riot.Active, riot.Riot, riot.Police) || (int)(frame - autoScanFrame) < 0) return;
            autoScanFrame = frame + 15;
            DevCrowdPresence presence = DevCrowdPresence.Current;
            if (presence == null || presence.Stamp != stamp) return;
            emptyAssaultArea=null;
            if (parkedRiot != stamp) { parkedRiot = stamp; parkedHits = 0; roadBudgets.Clear(); }
            VehicleManager manager = Singleton<VehicleManager>.instance;
            Vehicle[] cars = manager.m_vehicles.m_buffer;
            ushort selected = 0; float closest = 18 * 18;
            Vector3 selectedOrigin = riot.Position;
            for(int grid=0;grid<2;grid++) {
                int[] cells=grid==0 ? presence.Cells : presence.LargeCells;
                if(cells==null) continue;
                ushort[] vehicleGrid=grid==0 ? manager.m_vehicleGrid : manager.m_vehicleGrid2;
                foreach (int cell in cells)
                {
                    ushort id = vehicleGrid[cell];
                    // A corrupt modded grid must not create an unbounded scan or index outside the buffer.
                    for (int steps = 0; id != 0 && id < cars.Length && steps < 512; steps++)
                    {
                        Vehicle car = cars[id]; ushort next = car.m_nextGridVehicle;
                        Vector3 origin; float distance = presence.Near(car.GetLastFramePosition(), out origin);
                        if (Find(id)==null && !wrecks.ContainsKey(id) && distance <= 18 * 18
                            && riot.DevRoad != null && (riot.DevRoad.Constrain(car.GetLastFramePosition()) - car.GetLastFramePosition()).sqrMagnitude < 1
                            && distance < closest && DevVehicleAssaultRules.Eligible(car, car.Info != null && car.Info.m_vehicleAI!=null && DevVehicleAssaultRules.RoadVehicle(car.Info.m_vehicleAI.GetType())))
                        {
                            uint[] manifest = DevVehicleAssaultRules.Manifest(id, car.m_citizenUnits, Singleton<CitizenManager>.instance.m_units.m_buffer,
                                Singleton<CitizenManager>.instance.m_citizens.m_buffer, Singleton<CitizenManager>.instance.m_instances.m_buffer);
                            if (manifest != null && RoadAvailable(car.GetLastFramePosition(),false)) { selected = id; closest = distance; selectedOrigin = origin; }
                        }
                        id = next;
                    }
                }
            }
            if (selected == 0)
            {
                if (TryBeginParked(riot, stamp)) return;
                bool busy=false;
                foreach(DevVehicleAssault job in jobs) if(job.vehicle!=0) { busy=true; break; }
                if(!busy) emptyAssaultArea=presence;
                Status = "자동 습격 대기 · 18m 안 도로 차량 · 주차 피해 " + parkedHits + " · 잔해 " + wrecks.Count + "대"; return;
            }
            Vehicle target = cars[selected];
            Begin(new Request { Vehicle = selected, Info = target.m_infoIndex, Units = target.m_citizenUnits,
                Position = target.GetLastFramePosition(), Outcome = -1, Automatic = true, Origin = selectedOrigin }, stamp);
            if(vehicle!=0) ReserveRoad(target.GetLastFramePosition(),false);
        }
        private static readonly Dictionary<ulong,int[]> roadBudgets=new Dictionary<ulong,int[]>();
        private ulong attackRoadKey;
        private bool roadReserved, roadParked;
        private static ulong RoadKey(Vector3 position)
        {
            NetManager net=Singleton<NetManager>.instance; ushort best=0; float distance=float.MaxValue;
            int x=Mathf.Clamp((int)(position.x/64+135),0,269), z=Mathf.Clamp((int)(position.z/64+135),0,269);
            for(int row=Math.Max(0,z-1);row<=Math.Min(269,z+1);row++) for(int col=Math.Max(0,x-1);col<=Math.Min(269,x+1);col++) {
                ushort id=net.m_segmentGrid[row*270+col];
                for(int guard=0;id!=0 && id<net.m_segments.m_buffer.Length && guard<512;guard++) {
                    NetSegment road=net.m_segments.m_buffer[id];
                    if(road.Info!=null && UnrestRoads.Walkable(id)) {
                        Vector3 point=road.GetClosestPosition(position); float d=(point-position).sqrMagnitude;
                        if(Math.Abs(point.y-position.y)<4 && d<distance && d<(road.Info.m_halfWidth+3)*(road.Info.m_halfWidth+3)) { best=id; distance=d; }
                    }
                    id=road.m_nextGridSegment;
                }
            }
            return best==0 ? 0 : ((ulong)net.m_segments.m_buffer[best].m_buildIndex<<16)|best;
        }
        private static bool RoadAvailable(Vector3 p,bool parked) { ulong key=RoadKey(p); int[] counts; return key!=0 && (!roadBudgets.TryGetValue(key,out counts) || DevAssaultSchedulingRules.RoadAvailable(counts[parked?1:0])); }
        private void ReserveRoad(Vector3 p,bool parked) {
            attackRoadKey=RoadKey(p); roadParked=parked; roadReserved=attackRoadKey!=0;
            if(!roadReserved) return;
            int[] counts; if(!roadBudgets.TryGetValue(attackRoadKey,out counts)) { counts=new int[2]; roadBudgets.Add(attackRoadKey,counts); }
            counts[parked?1:0]++;
        }
        private void ReleaseRoadBudget() {
            int[] counts;
            if(roadReserved && seconds<2 && riotStamp==parkedRiot && roadBudgets.TryGetValue(attackRoadKey,out counts)) counts[roadParked?1:0]=Math.Max(0,counts[roadParked?1:0]-1);
            roadReserved=false;
        }
        private static bool WreckMatches(Wreck wreck, Vehicle car)
        { return car.m_infoIndex == wreck.Info && car.m_citizenUnits == wreck.Units && (car.m_flags & (Vehicle.Flags.Created | Vehicle.Flags.Spawned | Vehicle.Flags.Deleted)) == (Vehicle.Flags.Created | Vehicle.Flags.Spawned); }
        private static bool HeldWreck(ushort id, Vehicle car)
        {
            if (wreckIds == null || id >= wreckIds.Length || !wreckIds[id]) return false;
            lock (Gate) { Wreck wreck; return wrecks.TryGetValue(id, out wreck) && WreckMatches(wreck, car); }
        }
        private static bool WreckStatus(ushort id, Vehicle car, ref string text)
        {
            if (!HeldWreck(id, car)) return true;
            text = L.T("폭동 피해 차량 · 운행 불가 · 폭동 종료 후 순차 정리"); return false;
        }
        private void ArchiveWreck()
        {
            if (wreckIds == null) wreckIds = new bool[Singleton<VehicleManager>.instance.m_vehicles.m_buffer.Length];
            wrecks[vehicle] = new Wreck { Info = infoIndex, Units = units, Riot = riotStamp, Original = original, Flags = originalFlags, View = view };
            wreckIds[vehicle] = true; PublishWrecks();
            Forget("습격 완료 · 잔해 유지, 다음 차량 대기");
        }
        private static void PublishWrecks()
        {
            List<DevAssaultView> list = new List<DevAssaultView>();
            foreach (Wreck wreck in wrecks.Values) list.Add(wreck.View);
            wreckViews = list.ToArray();
        }
        private static void RemoveWreck(ushort id)
        { if (wrecks.Remove(id)) { wreckIds[id] = false; PublishWrecks(); } }
        private static void MaintainWrecks(long stamp)
        {
            foreach (DevAssaultView item in wreckViews)
            {
                Wreck wreck; if (!wrecks.TryGetValue(item.Vehicle, out wreck)) continue;
                if (wreck.Riot != stamp) {
                    uint frame=Singleton<SimulationManager>.instance.m_currentFrameIndex;
                    if(wreck.RetireAt==0) wreck.RetireAt=frame+1800;
                    if(DevAssaultSchedulingRules.Retire(frame,wreck.RetireAt,nextRetirement)) { TryReleaseWreck(item.Vehicle,wreck); nextRetirement=frame+180; }
                }
                else if (!WreckMatches(wreck, Singleton<VehicleManager>.instance.m_vehicles.m_buffer[item.Vehicle])) RemoveWreck(item.Vehicle);
            }
        }
        private static void ClearWrecks()
        {
            foreach (DevAssaultView item in wreckViews)
            { Wreck wreck; if (wrecks.TryGetValue(item.Vehicle, out wreck)) TryReleaseWreck(item.Vehicle, wreck); }
        }
        private static void TryReleaseWreck(ushort id, Wreck wreck)
        {
            try { ReleaseWreck(id, wreck); }
            catch (Exception error) { RemoveWreck(id); Debug.LogError("[CivicPressure] DEV wreck cleanup failed; native cleanup remains: " + error); }
        }
        private static void ReleaseWreck(ushort id, Wreck wreck)
        {
            VehicleManager manager = Singleton<VehicleManager>.instance;
            Vehicle car = manager.m_vehicles.m_buffer[id]; RemoveWreck(id);
            if (!WreckMatches(wreck, car)) return;
            CitizenManager citizens = Singleton<CitizenManager>.instance;
            uint[] manifest = DevVehicleAssaultRules.Manifest(id, wreck.Units, citizens.m_units.m_buffer, citizens.m_citizens.m_buffer, citizens.m_instances.m_buffer);
            if (manifest != null && manifest.Length == 0) manager.ReleaseVehicle(id);
            else
            {
                // Preserve passengers if another mod populated the wreck. Restore the native car instead of deleting it.
                manager.RemoveFromGrid(id, ref manager.m_vehicles.m_buffer[id], car.Info.m_isLargeVehicle);
                car.m_frame0 = car.m_frame1 = car.m_frame2 = car.m_frame3 = wreck.Original; car.m_flags = wreck.Flags;
                manager.m_vehicles.m_buffer[id] = car;
                manager.AddToGrid(id, ref manager.m_vehicles.m_buffer[id], car.Info.m_isLargeVehicle);
                car.Info.m_vehicleAI.FrameDataUpdated(id, ref manager.m_vehicles.m_buffer[id], ref manager.m_vehicles.m_buffer[id].m_frame0);
            }
        }
    }
}
