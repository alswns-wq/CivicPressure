using System;
using System.Collections.Generic;
using System.Reflection;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    // Owned convoy only; support identities persist with the incident.
    internal static partial class PoliceDeployment
    {
        private static UnrestEvent current;
        private static long nextPath;
        private static readonly MethodInfo StartPath = typeof(PoliceCarAI).GetMethod("StartPathFind", BindingFlags.NonPublic | BindingFlags.Instance,
            null, new Type[] { typeof(ushort), typeof(Vehicle).MakeByRefType() }, null);
        internal static Vector3 Destination { get { return new Vector3(current.X, current.Y, current.Z); } }
        internal static Vector3 Target(ushort id)
        {
            return DevTarget(id);
        }
        private static bool AtTarget(ushort id, Vector3 position)
        {
            return DevCanPark(id,position,false);
        }
        internal static void Reset() { current = null; nextPath = 0;
reinforced=0; DevCasualties.Reset(); DevClearTargets(); CivicUnrest.DevResetArmedSupport();
 }
        private static PoliceUnit[] Units(UnrestEvent e)
        {
            if (e == null || e.DeployedVehicle == 0) return new PoliceUnit[0];
            List<PoliceUnit> list = new List<PoliceUnit>();
            list.Add(new PoliceUnit { Vehicle = e.DeployedVehicle, Station = e.DeployedStation, StationIndex = e.StationIndex });
            list.AddRange(e.Support); return list.ToArray();
        }
        internal static bool Owns(ushort id)
        {
            if (current == null || id == 0) return false;
            if (current.DeployedVehicle == id) return true;
            foreach (PoliceUnit u in current.Support) if (u.Vehicle == id) return true;
            return false;
        }
        internal static void Promote(ushort id)
        {
            if (current == null || current.DeployedVehicle == id) return;
            for (int i = 0; i < current.Support.Length; i++) if (current.Support[i].Vehicle == id) {
                PoliceUnit old = new PoliceUnit { Vehicle = current.DeployedVehicle, Station = current.DeployedStation, StationIndex = current.StationIndex };
                SetLead(current, current.Support[i]); current.Support[i] = old; return;
            }
        }
        internal static bool Holding(ushort id)
        {
            return Owns(id) && current.Police && AtTarget(id, Singleton<VehicleManager>.instance.m_vehicles.m_buffer[id].GetLastFramePosition());
        }
        internal static void Assign(UnrestEvent e, ushort id, ushort station)
        {
            current = e;
            PoliceUnit u = new PoliceUnit { Vehicle = id, Station = station, StationIndex = Singleton<BuildingManager>.instance.m_buildings.m_buffer[station].m_buildIndex };
            if (e.DeployedVehicle == 0) SetLead(e, u);
            else { List<PoliceUnit> list = new List<PoliceUnit>(e.Support); list.Add(u); e.Support = list.ToArray(); }
            DevAssignTarget(id);
            Repath(u); nextPath = 0;
        }
        private static void SetLead(UnrestEvent e, PoliceUnit u)
        { e.DeployedVehicle = u.Vehicle; e.DeployedStation = u.Station; e.StationIndex = u.StationIndex; }
        private static bool Valid(UnrestEvent e, PoliceUnit u)
        {
            Vehicle[] cars = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            if (u.Vehicle == 0 || u.Vehicle >= cars.Length || u.Station == 0 || u.Station >= buildings.Length) return false;
            Vehicle car = cars[u.Vehicle];
            return (car.m_flags & Vehicle.Flags.Created) != 0 && car.m_sourceBuilding == u.Station && car.Info != null && car.Info.m_vehicleAI is PoliceCarAI &&
                CivicSimulation.Live(buildings[u.Station]) && buildings[u.Station].m_buildIndex == u.StationIndex && car.m_targetBuilding == e.Hall;
        }
        internal static bool Valid(UnrestEvent e)
        { foreach (PoliceUnit u in Units(e)) if (Valid(e, u)) return true; return false; }
        internal static void Tick(UnrestEvent e, DateTime now)
        {
            current = e;
            Vehicle[] cars = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            bool near = false;
            foreach (PoliceUnit u in Units(e))
            {
                if (!Valid(e, u)) { Released(u.Vehicle); continue; }
                if(!devTargets.ContainsKey(u.Vehicle)) DevAssignTarget(u.Vehicle);
                Vehicle car = cars[u.Vehicle];
                if ((car.m_flags & Vehicle.Flags.Spawned) != 0 && AtTarget(u.Vehicle, car.GetLastFramePosition()))
                {
                    near = true;
                    DevPark(u.Vehicle,ref cars[u.Vehicle]);
                }
                else if (now.Ticks >= nextPath && (car.m_flags & Vehicle.Flags.WaitingPath) == 0) {
                    bool retry=DevRefreshTarget(u.Vehicle);
                    if(!DevIsParked(u.Vehicle) && (retry || cars[u.Vehicle].m_path==0)) Repath(u);
                    if(DevIsParked(u.Vehicle)) near=true;
                }
            }
            if (!near) { e.Police = false; e.PoliceAt = 0; }
            if (now.Ticks >= nextPath) nextPath = (now.Ticks + GameClock.Minutes(5));
        }
        private static bool Near(Vector3 point)
        { return Near(current, point); }
        private static bool Near(UnrestEvent e, Vector3 point)
        {
            if (e == null) return false;
            Vector3 d = point - new Vector3(e.X, e.Y, e.Z);
            return Math.Abs(d.y) < 10 && d.x * d.x + d.z * d.z <= 32 * 32;
        }

        // Segments that stopped convoy cars are physically blocking. Bounded by the caller's buffer and
        // restricted to roads touching the crowd road, so a station or approach road is never closed.
        internal static int Occupied(UnrestEvent e, ushort crowdRoad, ushort[] roads, uint[] indices)
        {
            int count = 0;
            if (e == null || crowdRoad == 0 || roads == null || indices == null) return 0;
            NetSegment[] segments = Singleton<NetManager>.instance.m_segments.m_buffer;
            if (crowdRoad >= segments.Length) return 0;
            ushort startNode = segments[crowdRoad].m_startNode, endNode = segments[crowdRoad].m_endNode;
            Vehicle[] cars = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            PathUnit[] paths = Singleton<PathManager>.instance.m_pathUnits.m_buffer;
            foreach (PoliceUnit u in Units(e))
            {
                if (count >= roads.Length || count >= indices.Length) break;
                if (u.Vehicle == 0 || u.Vehicle >= cars.Length) continue;
                Vehicle car = cars[u.Vehicle];
                if ((car.m_flags & (Vehicle.Flags.Created | Vehicle.Flags.Spawned | Vehicle.Flags.Stopped)) !=
                    (Vehicle.Flags.Created | Vehicle.Flags.Spawned | Vehicle.Flags.Stopped) || car.m_path == 0) continue;
                if (!Near(e, car.GetLastFramePosition())) continue;
                PathUnit.Position position;
                if (!paths[car.m_path].GetPosition(car.m_pathPositionIndex == 255 ? 0 : car.m_pathPositionIndex >> 1, out position)) continue;
                ushort road = position.m_segment;
                if (road == 0 || road == crowdRoad || road >= segments.Length) continue;
                if ((segments[road].m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) != NetSegment.Flags.Created) continue;
                if (segments[road].m_startNode != startNode && segments[road].m_endNode != startNode &&
                    segments[road].m_startNode != endNode && segments[road].m_endNode != endNode) continue;
                bool seen = false;
                for (int i = 0; i < count; i++) if (roads[i] == road) { seen = true; break; }
                if (seen) continue;
                roads[count] = road; indices[count] = segments[road].m_buildIndex; count++;
            }
            return count;
        }
        internal static bool Arrive(ushort id, ref Vehicle car)
        {
            if (!Owns(id)) return false;
            if(!devTargets.ContainsKey(id)) DevAssignTarget(id);
            if(DevCanPark(id,car.GetLastFramePosition(),true)) DevPark(id,ref car);
            else if(DevRetryParking(id)) foreach(PoliceUnit unit in Units(current)) if(unit.Vehicle==id) Repath(unit);
            return true;
        }
        internal static void Freeze(ref Vehicle car)
        {
            car.m_flags |= Vehicle.Flags.Stopped | Vehicle.Flags.Emergency2;
            car.m_flags &= ~(Vehicle.Flags.Leaving | Vehicle.Flags.GoingBack | Vehicle.Flags.WaitingTarget);
            Vehicle.Frame frame = car.GetLastFrameData(); frame.m_velocity = Vector3.zero; frame.m_blinkState = 10;
            car.m_frame0 = car.m_frame1 = car.m_frame2 = car.m_frame3 = frame;
        }
        private static void Repath(PoliceUnit u)
        {
            if (current == null || !Owns(u.Vehicle) || !Valid(current, u)) return;
            Vehicle[] cars = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            ushort id = u.Vehicle;
            cars[id].m_flags &= ~(Vehicle.Flags.Arriving | Vehicle.Flags.Leaving | Vehicle.Flags.GoingBack | Vehicle.Flags.Stopped);
            cars[id].m_flags |= Vehicle.Flags.Emergency2;
            object[] args = { id, cars[id] };
            bool path = (bool)StartPath.Invoke(cars[id].Info.m_vehicleAI, args);
            cars[id] = (Vehicle)args[1];
            if (!path) Debug.LogWarning("[CivicPressure] Convoy path rejected: vehicle=" + id + " target=" + Target(id));
            cars[id].m_flags |= Vehicle.Flags.Emergency2;
        }
        internal static void Released(ushort id)
        {
            if (!Owns(id)) return;
            devTargets.Remove(id);
            DevArmedCars.Remove(id);
            List<PoliceUnit> list = new List<PoliceUnit>(Units(current));
            list.RemoveAll(delegate(PoliceUnit u) { return u.Vehicle == id; });
            if (list.Count == 0) { current.DeployedVehicle = current.DeployedStation = 0; current.Support = new PoliceUnit[0]; current.Police = false; current.PoliceAt = 0; }
            else { SetLead(current, list[0]); list.RemoveAt(0); current.Support = list.ToArray(); }
        }
        internal static void Release()
        {
            if (current == null) return;
            UnrestEvent e = current;
            List<PoliceUnit> valid = new List<PoliceUnit>();
            foreach (PoliceUnit u in Units(e)) if (Valid(e, u)) valid.Add(u);
            e.DeployedVehicle = e.DeployedStation = 0; e.Support = new PoliceUnit[0]; current = null;
            DevClearTargets();
            DevArmedCars.Clear();
            foreach (PoliceUnit u in valid)
            {
                try { Return(u.Vehicle); }
                catch (Exception error) { Debug.LogWarning("[CivicPressure] Convoy return: " + error.Message); }
            }
        }
        private static void Return(ushort id)
        {
            Vehicle[] cars = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            cars[id].m_flags &= ~(Vehicle.Flags.Stopped | Vehicle.Flags.Arriving | Vehicle.Flags.Leaving);
            cars[id].Info.m_vehicleAI.SetTarget(id, ref cars[id], 0);
            Singleton<TransferManager>.instance.RemoveIncomingOffer(TransferManager.TransferReason.Crime, new TransferManager.TransferOffer { Vehicle = id });
            cars[id].m_flags &= ~(Vehicle.Flags.WaitingTarget | Vehicle.Flags.Emergency2);
            cars[id].m_flags |= Vehicle.Flags.GoingBack;
            object[] args = { id, cars[id] };
            bool path = (bool)StartPath.Invoke(cars[id].Info.m_vehicleAI, args);
            cars[id] = (Vehicle)args[1];
            if (!path) cars[id].Unspawn(id);
        }
    }
}
