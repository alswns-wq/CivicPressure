using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    internal sealed class DevPoliceParking
    {
        internal Vector3 Target;
        internal int Slot, Side, Retries, NoProgress;
        internal float BestDistance=float.MaxValue;
        internal bool Parked;
    }
    internal static partial class PoliceDeployment
    {
        private static UnrestEvent devTargetEvent;
        private static readonly Dictionary<ushort, DevPoliceParking> devTargets = new Dictionary<ushort, DevPoliceParking>();
        private static void DevClearTargets() { devTargets.Clear(); devTargetEvent=null; }
        internal static bool DevIsParked(ushort id)
        { DevPoliceParking place; return devTargetEvent==current && devTargets.TryGetValue(id,out place) && place.Parked; }
        internal static int DevSide(ushort id)
        { DevPoliceParking place; return devTargets.TryGetValue(id,out place) ? place.Side : -1; }
        private static void DevAssignTarget(ushort id)
        {
            if (devTargetEvent != current) { DevClearTargets(); devTargetEvent=current; }
            int slot=0;
            while(true) {
                bool used=false; foreach(DevPoliceParking place in devTargets.Values) if(place.Slot==slot) { used=true; break; }
                if(!used) break; slot++;
            }
            devTargets[id]=new DevPoliceParking { Slot=slot };
            devTargets[id].Target=DevCalculateTarget(id);
        }
        private static Vector3 DevTarget(ushort id)
        { DevPoliceParking p; return devTargetEvent == current && devTargets.TryGetValue(id, out p) ? p.Target : Destination; }
        private static bool DevRoom(ushort id,Vector3 position)
        {
            Vehicle[] cars=Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            float length=cars[id].Info==null ? 6 : cars[id].Info.m_generatedInfo.m_size.z;
            foreach(KeyValuePair<ushort,DevPoliceParking> pair in devTargets) {
                if(pair.Key==id || !pair.Value.Parked) continue;
                Vehicle other=cars[pair.Key];
                if((other.m_flags & (Vehicle.Flags.Created|Vehicle.Flags.Spawned|Vehicle.Flags.Deleted))!=(Vehicle.Flags.Created|Vehicle.Flags.Spawned)) continue;
                float otherLength=other.Info==null ? 6 : other.Info.m_generatedInfo.m_size.z;
                Vector3 d=other.GetLastFramePosition()-position;
                if(Math.Abs(d.y)<4 && d.x*d.x+d.z*d.z<DevPoliceLineRules.Clearance(length,otherLength)*DevPoliceLineRules.Clearance(length,otherLength)) return false;
            }
            return true;
        }
        private static bool DevCanPark(ushort id,Vector3 position,bool endpoint)
        {
            DevPoliceParking place;
            if(!devTargets.TryGetValue(id,out place)) return false;
            if(place.Parked) return true;
            // A stopped lead car can make the reserved slot unreachable on any road.
            // Accept the follower's real reachable position near the established scene.
            if(endpoint && current!=null && current.Police && place.Slot>0) {
                Vector3 nearby=position-Destination;
                if(Math.Abs(nearby.y)<4 && nearby.x*nearby.x+nearby.z*nearby.z<=64*64) {
                    DevRoadRibbon ribbon=DevRoadCorridor.Make(current,Destination);
                    if(ribbon!=null && (ribbon.Constrain(position)-position).sqrMagnitude<=4 && DevRoom(id,position)) return true;
                }
            }
            Vector3 d=position-place.Target;
            return Math.Abs(d.y)<4 && d.x*d.x+d.z*d.z<=(endpoint ? 24*24 : 6*6) && DevRoom(id,position);
        }
        private static void DevPark(ushort id,ref Vehicle car)
        {
            DevPoliceParking place=devTargets[id]; place.Parked=true; place.Target=car.GetLastFramePosition();
            CivicUnrest.PoliceArrived(id,car.m_targetBuilding,place.Target); Freeze(ref car);
        }
        private static bool DevRefreshTarget(ushort id)
        {
            if(!devTargets.ContainsKey(id)) DevAssignTarget(id); // restored convoy
            DevPoliceParking place=devTargets[id]; if(place.Parked) return false;
            Vehicle[] cars=Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            Vector3 position=cars[id].GetLastFramePosition();
            float distance=(position-place.Target).magnitude;
            if(current.Police && place.Slot>0 && DevCanPark(id,position,true)) {
                DevPark(id,ref cars[id]); return false;
            }
            if(distance<80) {
                if(distance<place.BestDistance-2) { place.BestDistance=distance; place.NoProgress=0; }
                else if(++place.NoProgress>=2) {
                    if(DevCanPark(id,position,true)) { DevPark(id,ref cars[id]); return false; }
                    return DevRetryParking(id);
                }
            }
            // Keep reservations fixed once the first car has established the scene.
            if(current.Police || (place.Target-Destination).sqrMagnitude<80*80) return false;
            Vector3 next=DevCalculateTarget(id);
            if((next-place.Target).sqrMagnitude<12*12) return false;
            place.Target=next; return true;
        }
        private static bool DevRetryParking(ushort id)
        {
            DevPoliceParking place=devTargets[id];
            place.NoProgress=0; place.BestDistance=float.MaxValue;
            if(++place.Retries>3) {
                Released(id); Return(id);
                Debug.Log("[CivicPressure] Convoy returned after no reachable clear stopping place: "+id);
                return false;
            }
            place.Target=DevCalculateTarget(id); return true;
        }
        private static Vector3 DevCalculateTarget(ushort id)
        {
            if (current.Roads.Length == 0) return Destination;
            NetSegment road = Singleton<NetManager>.instance.m_segments.m_buffer[current.Roads[0]];
            bool forward=false, backward=false;
            if (road.Info != null) foreach (NetInfo.Lane lane in road.Info.m_lanes)
            {
                if ((lane.m_laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) == 0 || (lane.m_vehicleType & VehicleInfo.VehicleType.Car) == 0) continue;
                forward |= (lane.m_finalDirection & NetInfo.Direction.Forward) != 0;
                backward |= (lane.m_finalDirection & NetInfo.Direction.Backward) != 0;
            }
            // One-way roads keep the native legal approach; never reverse or teleport a car.
            DevRoadRibbon ribbon=DevRoadCorridor.Make(current,Destination);
            if(ribbon==null) return Destination;
            DevPoliceParking place=devTargets[id]; bool twoWay=forward && backward;
            int side=twoWay && place.Slot%2!=0 ? 1 : -1;
            place.Side=side;
            Vector3 heading, candidate=Destination;
            // Search distinct route positions, including near a route endpoint where clamping merges slots.
            for(int attempt=0;attempt<128;attempt++) {
                int direction;
                float station=ribbon.Front+DevPoliceLineRules.ParkingOffset(place.Slot,attempt,place.Retries,twoWay,out direction);
                candidate=ribbon.At(station,0,out heading);
                bool clear=true;
                foreach(KeyValuePair<ushort,DevPoliceParking> other in devTargets)
                    if(other.Key!=id && (other.Value.Target-candidate).sqrMagnitude<12*12) { clear=false; break; }
                if(clear) { place.Side=direction; return candidate; }
            }
            return candidate; // native queue/retry handles a full approach; never move a vehicle by teleport.
        }
        internal static Vector3[] DevEscapeDoors(UnrestEvent e, DevRoadRibbon ribbon)
        {
            if(ribbon==null) return new Vector3[0];
            List<Vector3> doors=new List<Vector3>();
            BuildingManager manager=Singleton<BuildingManager>.instance;
            Vector3 center=new Vector3(e.X,e.Y,e.Z);
            int x=Mathf.Clamp((int)(center.x/64+135),0,269), z=Mathf.Clamp((int)(center.z/64+135),0,269);
            for(int row=Math.Max(0,z-2);row<=Math.Min(269,z+2);row++)
                for(int col=Math.Max(0,x-2);col<=Math.Min(269,x+2);col++)
                {
                    ushort id=manager.m_buildingGrid[row*270+col];
                    for(int guard=0;id!=0 && guard<512;guard++)
                    {
                        Building b=manager.m_buildings.m_buffer[id]; id=b.m_nextGridBuilding;
                        if(!CivicSimulation.Live(b) || b.Info==null || b.m_fireIntensity!=0 || (b.m_flags & (Building.Flags.Collapsed|Building.Flags.Evacuating))!=0) continue;
                        ItemClass.Service service=b.Info.m_class.m_service;
                        if(service!=ItemClass.Service.Residential && service!=ItemClass.Service.Commercial && service!=ItemClass.Service.Office) continue;
                        Vector3 door=b.CalculateAccessPosition(0f,0f);
                        if((door-center).sqrMagnitude<=120*120 && (ribbon.Constrain(door)-door).sqrMagnitude<8*8) doors.Add(door);
                    }
                }
            return doors.ToArray();
        }
        internal static void DevDefeat(UnrestEvent e)
        {
            PoliceUnit[] cars=Units(e); int wanted=DevCombatRules.BurnCount(cars.Length), burned=0;
            // Preserve at least one retreat vehicle, even if every car is empty.
            for(int i=0;i<cars.Length-1 && burned<wanted;i++)
                if(Valid(e,cars[i]) && DevVehicleAssault.DevBurnEmptyPolice(cars[i].Vehicle,e.Started)) burned++;
        }
    }
    internal sealed class DevCombatResult
    {
        internal static volatile DevCombatResult Current;
        internal static volatile bool Resolving;
#if DEV
        internal static volatile DevCombatResult Requested;
        internal static bool CanRequest(UnrestView view)
        {
            return view!=null && view.Active && view.Riot && view.Police && view.PoliceVehicle!=0 &&
                view.DevPoliceUnits!=null && view.DevPoliceUnits.Length>0;
        }
        internal static void Request(bool policeWon)
        {
            UnrestView view=CivicUnrest.View;
            if(!CanRequest(view)) return;
            Requested=new DevCombatResult { Stamp=CivicUnrest.DevRiotStamp,Lead=view.PoliceVehicle,PoliceWon=policeWon };
        }
#endif
        internal long Stamp;
        internal ushort Lead;
        internal bool PoliceWon;
        internal static bool Matches(DevCombatResult result,long stamp,ushort lead)
        { return result!=null && result.Stamp==stamp && lead!=0 && result.Lead==lead; }
    }
    internal static class DevCombatRules
    {
        internal static int Crew(ushort car) { return 2+car%4; }
        internal static bool LiveRound(DevCombatant person,float now,double roll)
        {
            if(person.Dead || person.Gone || person.Detained || roll>=.35) return false;
            person.Dead=true; person.FallAt=now; person.DownUntil=float.MaxValue;
            person.Action=0; person.Moving=false;
            return true;
        }
        internal static int Resistance(int actor) { return 3+actor%3; }
        internal static bool Flees(int hits,int limit) { return hits>=limit; }
        internal static bool Down(float now,float until) { return now<until; }
        internal static bool Force(DevCombatant person,float now,bool rubber)
        {
            person.Hits++;
            if(rubber) person.DownUntil=now+10;
            return Flees(person.Hits,person.Limit);
        }
        internal static int Outcome(int resisting,int activePolice,int pendingCars)
        { return resisting==0 ? 1 : activePolice==0 && pendingCars==0 ? -1 : 0; }
        internal static int BurnCount(int cars) { return cars<=1 ? 0 : Math.Max(1,cars/2); }
    }
}
