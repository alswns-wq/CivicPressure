using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    internal sealed class DevMedic
    {
        internal DevTreatment Treatment;
        internal CrowdActor Actor;
        internal Vector3 Position;
        internal float Care;
        internal bool Returning;
        internal void Treat(float delta)
        {
            if(Returning || Treatment.Cancelled || Treatment.Dead || Treatment.Arrival==null) return;
            Care+=Mathf.Max(0,delta);
            if(Care>=8) { Treatment.Treated=true; Returning=true; }
        }
    }
    public sealed partial class CrowdPreview
    {
        private readonly List<DevMedic> devMedics=new List<DevMedic>();
        private CitizenInfo devMedicSkin;
        private bool devMedicScanned;
        private void DevResetMedics()
        {
            foreach(DevMedic medic in devMedics) { medic.Treatment.Departed=true; if(medic.Actor!=null) medic.Actor.Dispose(); }
            devMedics.Clear();
        }
        private void DevMedicTick(float delta)
        {
            foreach(DevCombatant officer in devPolice) {
                if(officer==null || officer.Treatment==null || officer.Treatment.Dead || officer.Treatment.Cancelled || officer.Treatment.Arrival==null) continue;
                DevTreatment care=officer.Treatment;
                bool found=false;
                foreach(DevMedic medic in devMedics) if(medic.Treatment==care) { found=true; break; }
                if(!found) devMedics.Add(new DevMedic { Treatment=care,Position=care.Arrival.Position });
            }
            int createBudget=1;
            for(int i=devMedics.Count-1;i>=0;i--) {
                DevMedic medic=devMedics[i]; DevTreatment care=medic.Treatment;
                if(care.Cancelled || care.Departed) { if(medic.Actor!=null) medic.Actor.Dispose(); devMedics.RemoveAt(i); continue; }
                Vector3 target=medic.Returning ? care.Arrival.Position : care.Position+Vector3.right*.8f;
                Vector3 direction=target-medic.Position; direction.y=0;
                bool walking=direction.sqrMagnitude>.16f;
                if(walking) medic.Position=Vector3.MoveTowards(medic.Position,target,delta*2.4f);
                else if(medic.Returning) { care.Departed=true; continue; }
                else medic.Treat(delta);
                // Logic continues outside the camera; only visible actors require a rig.
                bool visible=CrowdActor.InView(medic.Position);
                if(medic.Actor==null && visible && createBudget>0) {
                    if(!devMedicScanned) {
                        devMedicScanned=true;
                        for(uint n=0;n<PrefabCollection<CitizenInfo>.LoadedCount();n++) {
                            CitizenInfo info=PrefabCollection<CitizenInfo>.GetLoaded(n);
                            if(info!=null && info.m_class!=null && info.m_class.m_service==ItemClass.Service.HealthCare && info.m_skinRenderer!=null && info.m_agePhase==Citizen.AgePhase.Adult0) { devMedicSkin=info; break; }
                        }
                    }
                    medic.Actor=new CrowdActor(transform,devMedicSkin,i);
                    createBudget--;
                }
                if(medic.Actor==null) continue;
                medic.Actor.Show(visible); if(!visible) continue;
                Vector3 facing=walking ? direction : care.Position-medic.Position;
                facing.y=0;
                Quaternion rotation=facing.sqrMagnitude>.001f ? Quaternion.LookRotation(facing) : Quaternion.identity;
                medic.Actor.Pose(medic.Position,rotation,false,walking,false,animationTime,delta);
                medic.Actor.SetPlacardVisible(false);
                if(!walking && !medic.Returning) medic.Actor.MedicalPose(rotation,medic.Care);
            }
        }
    }
}
