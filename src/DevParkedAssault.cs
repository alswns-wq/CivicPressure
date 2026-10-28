using System;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    internal static class DevParkedAssaultRules
    {
        internal static bool Eligible(ushort id, VehicleParked car, Citizen owner, bool privateCar, bool entering)
        {
            int invalid = (int)(VehicleParked.Flags.Created | VehicleParked.Flags.Deleted | VehicleParked.Flags.Parking);
            return id != 0 && car.m_ownerCitizen != 0 && privateCar && !entering && (car.m_flags & invalid) == (int)VehicleParked.Flags.Created
                && (owner.m_flags & (Citizen.Flags.Created | Citizen.Flags.Dead)) == Citizen.Flags.Created
                && owner.m_parkedVehicle == id && owner.m_vehicle == 0;
        }
    }
    internal sealed partial class DevVehicleAssault
    {
        private static long parkedRiot;
        private static int parkedHits;
        private VehicleParked parkedOriginal;
        private bool TryBeginParked(UnrestView riot, long stamp)
        {
            VehicleManager manager = Singleton<VehicleManager>.instance;
            CitizenManager citizens = Singleton<CitizenManager>.instance;
            VehicleParked[] parked = manager.m_parkedVehicles.m_buffer;
            DevCrowdPresence presence = DevCrowdPresence.Current;
            if (presence == null || presence.Stamp != stamp) return false;
            ushort chosen = 0; int candidates = 0;
            foreach (int cell in presence.Cells)
                {
                    ushort id = manager.m_parkedGrid[cell];
                    for (int steps = 0; id != 0 && id < parked.Length && steps < 512; steps++)
                    {
                        VehicleParked car = parked[id]; ushort next = car.m_nextGridParked;
                        if (car.m_ownerCitizen != 0 && car.m_ownerCitizen < citizens.m_citizens.m_buffer.Length)
                        {
                            Citizen owner = citizens.m_citizens.m_buffer[car.m_ownerCitizen];
                            bool entering = owner.m_instance >= citizens.m_instances.m_buffer.Length ||
                                (owner.m_instance != 0 && (citizens.m_instances.m_buffer[owner.m_instance].m_flags & CitizenInstance.Flags.EnteringVehicle) != 0);
                            VehicleInfo info = car.Info; Vector3 origin;
                            if (presence.Near(car.m_position, out origin) <= 18 * 18 &&
                                riot.DevRoad != null && (riot.DevRoad.Constrain(car.m_position) - car.m_position).sqrMagnitude < 1 &&
                                DevParkedAssaultRules.Eligible(id, car, owner, info != null && info.m_vehicleAI != null && info.m_vehicleAI.GetType() == typeof(PassengerCarAI)
                                    && (info.m_trailers == null || info.m_trailers.Length == 0), entering))
                            { if (RoadAvailable(car.m_position,true) && Random.Next(++candidates) == 0) chosen = id; }
                        }
                        id = next;
                    }
                }
            if (chosen == 0) return false;
            VehicleParked source = parked[chosen]; VehicleInfo prefab = source.Info;
            ushort empty;
            // Allocate first: a full moving-vehicle buffer must leave the owner's parked car intact.
            if (!manager.CreateVehicle(out empty, ref Singleton<SimulationManager>.instance.m_randomizer, prefab, source.m_position, TransferManager.TransferReason.None, false, false)) return false;
            bool committed = false;
            try
            {
                Citizen[] people = citizens.m_citizens.m_buffer;
                if (parked[chosen].m_infoIndex != source.m_infoIndex || parked[chosen].m_ownerCitizen != source.m_ownerCitizen || people[source.m_ownerCitizen].m_parkedVehicle != chosen) return false;
                Vehicle.Frame frame = new Vehicle.Frame(source.m_position, source.m_rotation);
                manager.m_vehicles.m_buffer[empty].m_frame0 = manager.m_vehicles.m_buffer[empty].m_frame1 = manager.m_vehicles.m_buffer[empty].m_frame2 = manager.m_vehicles.m_buffer[empty].m_frame3 = frame;
                manager.m_vehicles.m_buffer[empty].m_flags |= Vehicle.Flags.Stopped | Vehicle.Flags.Congestion;
                uint[] manifest = DevVehicleAssaultRules.Manifest(empty, manager.m_vehicles.m_buffer[empty].m_citizenUnits, citizens.m_units.m_buffer, people, citizens.m_instances.m_buffer);
                if (manifest == null || manifest.Length != 0) return false;
                manager.m_vehicles.m_buffer[empty].Spawn(empty);
                // Native release removes the parked grid entry AND the owner's parked-vehicle reference.
                manager.ReleaseParkedVehicle(chosen);
                parkedOriginal = source;
                vehicle = empty; infoIndex = manager.m_vehicles.m_buffer[empty].m_infoIndex; units = manager.m_vehicles.m_buffer[empty].m_citizenUnits;
                // A passenger added by another mod must be returned to normal AI, never to Congestion auto-delete.
                original = frame; originalFlags = manager.m_vehicles.m_buffer[empty].m_flags & ~(Vehicle.Flags.Congestion | Vehicle.Flags.Stopped);
                height = Math.Max(.8f, Math.Min(3, prefab.m_generatedInfo.m_size.y)); width = Math.Max(1, Math.Min(4, prefab.m_generatedInfo.m_size.x));
                length = Math.Max(2, Math.Min(8, prefab.m_generatedInfo.m_size.z)); ResetArrival();
                finalRoll = DevVehicleAssaultRules.FinalRoll(Random.Next(3), Random.Next(2) == 0);
                fireYaw = finalRoll == 0 ? (float)(Random.NextDouble() * 360) : 0;
                driving = approaching = false; automatic = fromParked = true; presence.Near(source.m_position, out assaultOrigin);
                seconds = 0; riotStamp = stamp; lastFrame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
                ReserveRoad(source.m_position,true);
                Track();
                parkedHits++; committed = true;
                Status = "주차 차량 자동 습격 · " + parkedHits;
                return true;
            }
            finally { if (!committed) manager.ReleaseVehicle(empty); }
        }
        private void RestoreUnattackedParked(VehicleInfo prefab)
        {
            if (!fromParked || seconds > 0) return;
            uint ownerId = parkedOriginal.m_ownerCitizen;
            Citizen[] citizens = Singleton<CitizenManager>.instance.m_citizens.m_buffer;
            if (ownerId == 0 || ownerId >= citizens.Length) return;
            Citizen owner = citizens[ownerId];
            if ((owner.m_flags & (Citizen.Flags.Created | Citizen.Flags.Dead)) != Citizen.Flags.Created || owner.m_vehicle != 0 || owner.m_parkedVehicle != 0) return;
            VehicleManager manager = Singleton<VehicleManager>.instance;
            ushort restored;
            if (!manager.CreateParkedVehicle(out restored, ref Singleton<SimulationManager>.instance.m_randomizer, prefab, parkedOriginal.m_position, parkedOriginal.m_rotation, 0))
            { Debug.LogWarning("[CivicPressure] DEV cancelled parked assault: no parked slot to restore; owner's next car uses native demand."); return; }
            manager.m_parkedVehicles.m_buffer[restored].m_flags = parkedOriginal.m_flags;
            manager.m_parkedVehicles.m_buffer[restored].m_travelDistance = parkedOriginal.m_travelDistance;
            citizens[ownerId].SetParkedVehicle(ownerId, restored);
            parkedHits = Math.Max(0, parkedHits - 1);
        }
    }
}
