using System;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    internal static class TrafficRouting
    {
        private static int cursor = 1;
        private static readonly uint[] retryAt = new uint[65536];
        internal static void Reset() { cursor = 1; Array.Clear(retryAt, 0, retryAt.Length);
            DevRiotPassage.Reset();
        }
        internal static bool Emergency(VehicleInfo.VehicleCategory category)
        { return category != VehicleInfo.VehicleCategory.None && (category & ~VehicleInfo.VehicleCategory.Emergency) == 0; }
        // Called from the path-finding thread: reads only the immutable published closure.
        internal static bool Holds(Closure c, ushort road, uint buildIndex)
        {
            if (c == null || road == 0) return false;
            if (c.Road == road) return c.BuildIndex == buildIndex;
            ushort[] held = c.Held; uint[] indices = c.HeldIndices;
            if (held == null || indices == null) return false;
            for (int i = 0; i < held.Length && i < indices.Length; i++)
                if (held[i] == road) return indices[i] == buildIndex;
            return false;
        }
        internal static bool Closed(Closure c, ushort road)
        {
            if (c == null || road == 0) return false;
            NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
            return road < roads.Length && Holds(c, road, roads[road].m_buildIndex);
        }
        internal static bool Block(Closure c, ushort road, ref NetSegment segment, bool vehicle, VehicleInfo.VehicleType types, VehicleInfo.VehicleCategory category)
        {
            return Holds(c, road, segment.m_buildIndex) && vehicle &&
                (types & VehicleInfo.VehicleType.Car) != 0 && !Emergency(category) &&
                (segment.m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) == NetSegment.Flags.Created;
        }
        internal static bool Exempt(Vehicle car)
        {
            VehicleInfo info = car.Info;
            return info == null || !(info.m_vehicleAI is CarAI) || info.m_vehicleAI is PoliceCarAI ||
                (car.m_flags & Vehicle.Flags.Emergency2) != 0 || info.m_class.m_service == ItemClass.Service.FireDepartment ||
                info.m_class.m_service == ItemClass.Service.HealthCare;
        }
        private static bool Current(Vehicle car, out ushort road)
        {
            road = 0;
            if (car.m_path == 0) return false;
            PathUnit.Position position;
            if (!Singleton<PathManager>.instance.m_pathUnits.m_buffer[car.m_path].GetPosition(car.m_pathPositionIndex == 255 ? 0 : car.m_pathPositionIndex >> 1, out position)) return false;
            road = position.m_segment; return true;
        }
        internal static bool Inside(Vehicle car, ushort road)
        { ushort at; return Current(car, out at) && at == road; }
        // A car already standing inside the cordon must still be allowed to drive out.
        internal static bool Inside(Vehicle car, Closure c)
        { ushort at; return Current(car, out at) && Closed(c, at); }
        internal static void Tick()
        {
            Closure c = CivicUnrest.Closure;
            if (c == null) return;
            NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
            if (c.Road >= roads.Length || roads[c.Road].m_buildIndex != c.BuildIndex) return;
            Vehicle[] cars = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            uint frame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
            Vector3 center = roads[c.Road].m_middlePosition;
            float reach = roads[c.Road].m_averageLength;
            for (int i = 0; i < c.Held.Length; i++)
            {
                ushort held = c.Held[i];
                if (held != 0 && held < roads.Length) reach = Math.Max(reach, roads[c.Road].m_averageLength * 0.5f + roads[held].m_averageLength);
            }
            float radius = Math.Max(200, reach + 80);
            int changed = 0;
            for (int budget = 0; budget < 512 && changed < 4; budget++)
            {
                if (cursor >= cars.Length || cursor >= retryAt.Length) cursor = 1;
                ushort id = (ushort)cursor++;
                Vehicle car = cars[id];
                if (DevRiotPassage.Car(c, car)) continue;
                if ((car.m_flags & (Vehicle.Flags.Created | Vehicle.Flags.Spawned | Vehicle.Flags.WaitingPath)) != (Vehicle.Flags.Created | Vehicle.Flags.Spawned) ||
                    car.m_leadingVehicle != 0 || car.m_path == 0 || Exempt(car) || unchecked((int)(frame - retryAt[id])) < 0 ||
                    (car.GetLastFramePosition() - center).sqrMagnitude > radius * radius || Inside(car, c)) continue;
                uint path = car.m_path;
                int index = car.m_pathPositionIndex == 255 ? 0 : car.m_pathPositionIndex >> 1;
                bool approaching = false;
                for (int look = 0; look < 4; look++) {
                    PathUnit.Position position; bool invalid;
                    if (!PathUnit.GetNextPosition(ref path, ref index, out position, out invalid)) break;
                    if (Closed(c, position.m_segment)) { approaching = true; break; }
                }
                if (!approaching) continue;
                retryAt[id] = frame + 512;
                // The game's path reset preserves the destination and lets its lane/node routing turn the car.
                car.Info.m_vehicleAI.InvalidatePath(id, ref cars[id], id, ref cars[id]);
                changed++;
            }
        }
    }
}
