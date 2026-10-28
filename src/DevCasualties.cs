using System;
using System.Collections.Generic;
using System.Reflection;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    internal sealed class DevTreatment
    {
        internal long Stamp; internal bool Dead;
        internal Vector3 Position;
        internal ushort Vehicle, Hospital, Target;
        internal uint BuildIndex, Arrived, Started;
        internal volatile bool Complete;
        internal volatile bool Treated, Departed, Cancelled;
        internal volatile DevMedicalArrival Arrival;
    }
    internal sealed class DevMedicalArrival { internal Vector3 Position; }
    internal sealed class DevBackupRequest
    {
        internal long Stamp; internal int TotalCars;
        private uint started, nextAttempt; private bool attempted;
        internal volatile bool Complete;
        internal bool Due(uint now) { return !Complete && (!attempted || (int)(now-nextAttempt)>=0); }
        internal void FinishAttempt(uint now,int cars,bool valid)
        {
            if(!attempted) { started=now; attempted=true; }
            nextAttempt=now+192; TotalCars=cars;
            Complete=cars>0 || now-started>=1800 || !valid;
        }
    }
    internal static partial class PoliceDeployment
    {
        private static long reinforced;
        internal static readonly Dictionary<ushort,bool> DevArmedCars=new Dictionary<ushort,bool>();
        internal static int DevRecruitSupport(List<ushort> stations,int maximum)
        {
            if(current==null) return 0;
            Vehicle[] cars=Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            int count=0;
            for(int i=1;i<cars.Length && count<maximum;i++) {
                Vehicle car=cars[i];
                if(Owns((ushort)i) || car.Info==null || !(car.Info.m_vehicleAI is PoliceCarAI) || car.m_transferSize!=0 || !stations.Contains(car.m_sourceBuilding)) continue;
                if((car.m_flags & (Vehicle.Flags.Created|Vehicle.Flags.Spawned|Vehicle.Flags.Deleted|Vehicle.Flags.GoingBack|Vehicle.Flags.Emergency2))!=(Vehicle.Flags.Created|Vehicle.Flags.Spawned)) continue;
                Building station=Singleton<BuildingManager>.instance.m_buildings.m_buffer[car.m_sourceBuilding];
                if(!CivicSimulation.Live(station) || (station.m_flags & Building.Flags.Active)==0 || station.m_fireIntensity!=0) continue;
                Singleton<TransferManager>.instance.RemoveIncomingOffer(TransferManager.TransferReason.Crime,new TransferManager.TransferOffer { Vehicle=(ushort)i });
                car.Info.m_vehicleAI.SetTarget((ushort)i,ref cars[i],current.Hall);
                Assign(current,(ushort)i,car.m_sourceBuilding); DevArmedCars[(ushort)i]=true;
                if(CivicRecords.Draft!=null) CivicRecords.Draft.Cars++;
                count++;
                Debug.Log("[CivicPressure] Armed patrol reinforcement: vehicle="+i+", station="+car.m_sourceBuilding);
            }
            return count;
        }
        internal static void DevReinforce(long stamp)
        {
            if(current==null || current.Started!=stamp || !current.Riot || reinforced==stamp) return;
            reinforced=stamp;
            Vehicle[] cars=Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            byte district=Singleton<DistrictManager>.instance.GetDistrict(Destination);
            for(int i=1;i<cars.Length;i++) {
                Vehicle car=cars[i];
                if(Owns((ushort)i) || car.Info==null || !(car.Info.m_vehicleAI is PoliceCarAI) || car.m_sourceBuilding==0 || car.m_transferSize!=0) continue;
                if((car.m_flags & (Vehicle.Flags.Created|Vehicle.Flags.Spawned|Vehicle.Flags.Deleted|Vehicle.Flags.GoingBack|Vehicle.Flags.Emergency2))!=(Vehicle.Flags.Created|Vehicle.Flags.Spawned)) continue;
                if(Singleton<DistrictManager>.instance.GetDistrict(car.GetLastFramePosition())!=district) continue;
                if(!CivicSimulation.Live(Singleton<BuildingManager>.instance.m_buildings.m_buffer[car.m_sourceBuilding])) continue;
                Singleton<TransferManager>.instance.RemoveIncomingOffer(TransferManager.TransferReason.Crime,new TransferManager.TransferOffer { Vehicle=(ushort)i });
                car.Info.m_vehicleAI.SetTarget((ushort)i,ref cars[i],current.Hall);
                Assign(current,(ushort)i,car.m_sourceBuilding);
                Debug.Log("[CivicPressure] Officer-down reinforcement: "+i);
            }
        }
        internal static ushort DevMedicalTarget(long stamp) { return current!=null && current.Started==stamp ? current.Hall : (ushort)0; }
    }
    // All native service mutations run on the simulation thread. Clone recovery is a volatile signal.
    internal static class DevCasualties
    {
        internal static volatile DevBackupRequest Backup;
        private static readonly Queue<DevTreatment> requests=new Queue<DevTreatment>();
        private static readonly List<DevTreatment> cases=new List<DevTreatment>();
        // Native hooks run for every ambulance, including unrelated city traffic.
        private static readonly Dictionary<ushort,DevTreatment> vehicles=new Dictionary<ushort,DevTreatment>();
        private static readonly List<ushort> nearbyHospitals=new List<ushort>(), closeHospitals=new List<ushort>();
        private static readonly List<ushort> hospitalIds=new List<ushort>();
        private static uint hospitalFrame=uint.MaxValue;
        private static uint medicalDispatchFrame=uint.MaxValue;
        private static readonly MethodInfo path=typeof(AmbulanceAI).GetMethod("StartPathFind",BindingFlags.NonPublic|BindingFlags.Instance,null,new Type[]{typeof(ushort),typeof(Vehicle).MakeByRefType()},null);
        internal static DevTreatment Request(long stamp,Vector3 position,bool dead)
        {
            DevTreatment request=new DevTreatment { Stamp=stamp,Position=position,Dead=dead };
            lock(requests) requests.Enqueue(request);
            return request;
        }
        internal static DevTreatment Find(ushort id)
        {
            DevTreatment c;
            return vehicles.TryGetValue(id,out c) && Valid(c) ? c : null;
        }
        private static bool Valid(DevTreatment c)
        {
            if(c.Vehicle==0) return false;
            Vehicle v=Singleton<VehicleManager>.instance.m_vehicles.m_buffer[c.Vehicle];
            Building b=Singleton<BuildingManager>.instance.m_buildings.m_buffer[c.Hospital];
            return (v.m_flags & Vehicle.Flags.Created)!=0 && v.Info!=null && v.Info.m_vehicleAI is AmbulanceAI && v.m_sourceBuilding==c.Hospital && v.m_targetBuilding==c.Target && b.m_buildIndex==c.BuildIndex;
        }
        internal static void Tick()
        {
            uint now=Singleton<SimulationManager>.instance.m_currentFrameIndex;
            DevBackupRequest backup=Backup;
            if(backup!=null && backup.Due(now)) {
                backup.FinishAttempt(now,CivicUnrest.DevArmedSupport(backup.Stamp),backup.Stamp==CivicUnrest.DevRiotStamp);
                if(backup.Complete) Debug.Log("[CivicPressure] Armed support request completed: added="+backup.TotalCars);
            }
            while(true) {
                DevTreatment c;
                lock(requests) { if(requests.Count==0) break; c=requests.Dequeue(); }
                if(c.Stamp!=CivicUnrest.DevRiotStamp || !CivicUnrest.View.Active) continue;
                if(c.Dead) PoliceDeployment.DevReinforce(c.Stamp);
                else { c.Started=now; cases.Add(c); }
            }
            for(int i=cases.Count-1;i>=0;i--) {
                DevTreatment c=cases[i];
                bool ended=c.Stamp!=CivicUnrest.DevRiotStamp || !CivicUnrest.View.Active;
                if(ended || now-c.Started>18000 || (c.Vehicle!=0 && !Valid(c))) { c.Cancelled=true; Return(c); cases.RemoveAt(i); continue; }
                if(c.Vehicle==0) continue;
                Vehicle[] cars=Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
                if((cars[c.Vehicle].m_flags & Vehicle.Flags.Spawned)!=0 && (cars[c.Vehicle].GetLastFramePosition()-c.Position).sqrMagnitude<24*24) {
                    PoliceDeployment.Freeze(ref cars[c.Vehicle]);
                    if(c.Arrival==null) { c.Arrived=now; c.Arrival=new DevMedicalArrival { Position=cars[c.Vehicle].GetLastFramePosition() }; }
                    if(c.Treated) c.Complete=true;
                    if(c.Departed) { Return(c); cases.RemoveAt(i); }
                }
            }
            // Bound native ambulance traffic and serve waiting injuries oldest first.
            if((now&63)==0 && (medicalDispatchFrame==uint.MaxValue || now-medicalDispatchFrame>=192)) {
                int active=0; DevTreatment waiting=null;
                foreach(DevTreatment c in cases) {
                    if(c.Vehicle!=0) active++;
                    else if(waiting==null) waiting=c;
                }
                if(active<3 && waiting!=null) {
                    Dispatch(waiting);
                    if(waiting.Vehicle!=0) medicalDispatchFrame=now;
                }
            }
        }
        private static void Dispatch(DevTreatment c)
        {
            c.Target=PoliceDeployment.DevMedicalTarget(c.Stamp); if(c.Target==0) return;
            Building[] buildings=Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            VehicleManager vm=Singleton<VehicleManager>.instance;
            nearbyHospitals.Clear(); closeHospitals.Clear();
            float best=float.MaxValue;
            uint frame=Singleton<SimulationManager>.instance.m_currentFrameIndex;
            if(hospitalFrame!=frame) {
                hospitalFrame=frame; hospitalIds.Clear();
                for(int i=1;i<buildings.Length;i++) if(buildings[i].Info!=null && buildings[i].Info.m_buildingAI is HospitalAI) hospitalIds.Add((ushort)i);
            }
            foreach(ushort i in hospitalIds) {
                Building b=buildings[i]; HospitalAI ai=b.Info==null ? null : b.Info.m_buildingAI as HospitalAI;
                if(ai==null || !CivicSimulation.Live(b) || (b.m_flags & Building.Flags.Active)==0 || b.m_fireIntensity!=0) continue;
                int count=0; ushort id=b.m_ownVehicles;
                for(int guard=0;id!=0 && guard<vm.m_vehicles.m_buffer.Length;guard++) { Vehicle v=vm.m_vehicles.m_buffer[id]; if(v.Info!=null && v.Info.m_vehicleAI is AmbulanceAI) count++; id=v.m_nextOwnVehicle; }
                if(count>=ai.m_ambulanceCount) continue;
                float distance=(b.m_position-c.Position).sqrMagnitude;
                if(distance<best) best=distance;
                nearbyHospitals.Add((ushort)i);
            }
            if(nearbyHospitals.Count==0) return;
            foreach(ushort id in nearbyHospitals) if((buildings[id].m_position-c.Position).sqrMagnitude<=best+500*500) closeHospitals.Add(id);
            c.Hospital=closeHospitals[Singleton<SimulationManager>.instance.m_randomizer.Int32((uint)closeHospitals.Count)];
            Building hospital=buildings[c.Hospital]; c.BuildIndex=hospital.m_buildIndex;
            VehicleInfo info=vm.GetRandomVehicleInfo(ref Singleton<SimulationManager>.instance.m_randomizer,hospital.Info.m_class.m_service,hospital.Info.m_class.m_subService,hospital.Info.m_class.m_level);
            if(info==null || !(info.m_vehicleAI is AmbulanceAI)) return;
            ushort vehicle;
            if(!vm.CreateVehicle(out vehicle,ref Singleton<SimulationManager>.instance.m_randomizer,info,hospital.m_position,TransferManager.TransferReason.Sick,true,false)) return;
            c.Vehicle=vehicle;
            vehicles[vehicle]=c;
            info.m_vehicleAI.SetSource(vehicle,ref vm.m_vehicles.m_buffer[vehicle],c.Hospital);
            info.m_vehicleAI.SetTarget(vehicle,ref vm.m_vehicles.m_buffer[vehicle],c.Target);
            Debug.Log("[CivicPressure] Officer ambulance: vehicle="+vehicle+" hospital="+c.Hospital);
        }
        private static void Return(DevTreatment c)
        {
            vehicles.Remove(c.Vehicle);
            if(!Valid(c)) return;
            Vehicle[] cars=Singleton<VehicleManager>.instance.m_vehicles.m_buffer; ushort id=c.Vehicle;
            c.Vehicle=0; // Stop interception before asking native AI to return.
            cars[id].m_flags &= ~(Vehicle.Flags.Stopped|Vehicle.Flags.Arriving|Vehicle.Flags.Emergency2);
            cars[id].Info.m_vehicleAI.SetTarget(id,ref cars[id],0);
            Singleton<TransferManager>.instance.RemoveIncomingOffer(TransferManager.TransferReason.Sick,new TransferManager.TransferOffer { Vehicle=id });
            cars[id].m_flags &= ~Vehicle.Flags.WaitingTarget; cars[id].m_flags |= Vehicle.Flags.GoingBack;
            object[] args={id,cars[id]}; bool ok=(bool)path.Invoke(cars[id].Info.m_vehicleAI,args); cars[id]=(Vehicle)args[1];
            if(!ok) cars[id].Unspawn(id);
        }
        internal static void Reset()
        {
            foreach(DevTreatment c in cases) c.Cancelled=true;
            Backup=null; PoliceDeployment.DevArmedCars.Clear();
            cases.Clear(); vehicles.Clear(); nearbyHospitals.Clear(); closeHospitals.Clear(); hospitalIds.Clear(); hospitalFrame=medicalDispatchFrame=uint.MaxValue;
            lock(requests) requests.Clear();
        }
    }
}
