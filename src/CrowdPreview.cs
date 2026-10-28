using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    public sealed partial class CrowdPreview : MonoBehaviour
    {
        private readonly List<CrowdActor> people = new List<CrowdActor>();
        private readonly List<CitizenInfo> skins = new List<CitizenInfo>();
        private bool scanned, failed, positioned;
        private Vector3 center, lastPosition, direction = Vector3.forward;
        private float animationTime;
        private Quaternion devCrowdFacing;
        private bool devFacingSet;
#if DEV
        private int devProfileFrames, devProfileVisible;
        private long devProfileTotal, devProfileMotion;
        private float devProfileAt;
#endif
        private float nextPose, pendingDelta;
        private bool wasRiot, wasPolice;
        private readonly List<CrowdActor> officers = new List<CrowdActor>();
        private readonly List<CitizenInfo> policeSkins = new List<CitizenInfo>();

        public void Update()
        {
            if (failed) return;
#if DEV
            long profileStart=0, motionTicks=0; bool profileVisible=false;
#endif
            try
            {
                UnrestView state = CivicUnrest.View;
#if DEV
                if(state.Active && state.Riot) {
                    profileStart=System.Diagnostics.Stopwatch.GetTimestamp();
                    if(devProfileFrames==0) devProfileAt=Time.realtimeSinceStartup;
                } else { devProfileFrames=devProfileVisible=0; devProfileTotal=devProfileMotion=0; }
#endif
                if (!state.Active) { DevSuppressionReset(); DevStopAudio(); DevResetProtest(); }
                if (!state.Active)
                {
                    foreach (CrowdActor actor in people) actor.Show(false);
                    ShowOfficers(false); nextPose=0; pendingDelta=0; positioned = false;
                    devFacingSet = false;
                    DevResetRiotCrowd();
                    return;
                }
                SimulationManager simulation = Singleton<SimulationManager>.instance;
                bool paused = simulation.SimulationPaused || simulation.ForcedSimulationPaused;
                float animationDelta = DevVisualMotion.FrameDelta(simulation.m_simulationTimeDelta, paused);
                animationTime += animationDelta;
                if (!positioned) { center = lastPosition = state.Position; positioned = true; }
                Vector3 step = state.Position - lastPosition; step.y = 0;
                if (step.sqrMagnitude > .00001f)
                {
                    direction = step.normalized; lastPosition = state.Position;
                }
                bool moving = !paused && !state.Police && (state.Position - center).sqrMagnitude > .000025f;
                center = Vector3.Lerp(center, state.Position, paused ? 0 : DevVisualMotion.Blend(Time.unscaledDeltaTime, 12));
                Camera camera = Camera.main;
                DevAudioTick(state,paused,camera);
                if (state.Riot) {
                    if(devProtestRoad!=null) DevResetProtest();
#if DEV
                    long motionStart=System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                    DevAdvanceWalkers(state, animationDelta); DevCombatTick(state, animationDelta);
                    DevDrawDeaths();
#if DEV
                    motionTicks=System.Diagnostics.Stopwatch.GetTimestamp()-motionStart;
#endif
                }
                else DevProtestTick(state.DevRoad,animationDelta);
                if (!scanned) FindSkins();
                int devCount = UnrestRules.CrowdCount(state.Riot);
                for (int budget = 0; budget < 2 && people.Count < devCount; budget++)
                    people.Add(new CrowdActor(transform, skins.Count == 0 ? null : skins[people.Count % skins.Count], people.Count));
                bool visible = Visible(camera,center) || (state.Police && Visible(camera,state.PolicePosition));
                if (state.Riot && !visible)
                    for (int i = 0; i < 88 && !visible; i++) if (devRiotPlaced[i]) visible = Visible(camera, devRiotPlaces[i]);
#if DEV
                profileVisible=visible;
#endif
                if (!visible) {
                    DevHideSuppressionEffects();
                    foreach (CrowdActor actor in people) actor.Show(false); ShowOfficers(false); nextPose=0;pendingDelta=0;return;
                }
                DevSuppressionEffects();
                pendingDelta+=animationDelta;
                if(Time.realtimeSinceStartup<nextPose && wasRiot==state.Riot && wasPolice==state.Police)return;
                float distance=camera==null ? 0 : (camera.transform.position-center).sqrMagnitude;
                if(camera!=null && state.Police)distance=Math.Min(distance,(camera.transform.position-state.PolicePosition).sqrMagnitude);
                nextPose=Time.realtimeSinceStartup+DevVisualMotion.Interval(distance);
                wasRiot=state.Riot;wasPolice=state.Police;
                animationDelta=Math.Min(.2f,pendingDelta);pendingDelta=0;
                if (!scanned) FindSkins();
                int count = UnrestRules.CrowdCount(state.Riot);
                // Build only two visual skeletons per frame and reuse them across events.
                Quaternion facing = Quaternion.LookRotation(direction);
                if (devSuppressing) facing = Quaternion.LookRotation(-devForward);
                devCrowdFacing = devFacingSet ? Quaternion.Slerp(devCrowdFacing, facing, paused ? 0 : DevVisualMotion.Blend(Time.unscaledDeltaTime, 12)) : facing;
                devFacingSet = true; facing = devCrowdFacing;
                Police(state, animationDelta);
                if (!state.Riot) DevResetRiotCrowd();
                DevAssaultView assault = DevVehicleAssault.View;
                for (int i = 0; i < people.Count; i++)
                {
                    CrowdActor actor = people[i];
                    if (i >= count) { actor.Show(false); continue; }
                    if (devRiotBorrowed[i]) continue;
                    // Combat owns its pose. Do not evaluate the same animator twice,
                    // or reactivate an actor that has already entered a building.
                    if(state.Riot && DevCombatControls(i)) {
                        DevSuppressionCrowd(i,actor,facing,animationDelta);
                        continue;
                    }
                    actor.SetPlacardVisible(true);
                    int columns = UnrestRules.CrowdColumns(state.Riot);
                    float agitation = state.Riot && !state.Police ? .17f : 0;
                    Vector3 offset = new Vector3((i % columns - (columns - 1) * .5f) * 1.2f + agitation * Mathf.Sin(animationTime * 4 + i),
                        .04f, (i / columns - (count / columns - 1) * .5f) * 1.2f + agitation * Mathf.Sin(animationTime * 5 + i * 2));
                    Vector3 actorPosition = center + facing * offset;
                    Quaternion actorFacing = facing; bool actorMoving = moving;
                    if (state.Riot) actorMoving = DevRiotPose(i, facing, animationDelta, assault, out actorPosition, out actorFacing);
                    else if(!DevProtestPose(i,animationDelta,out actorPosition,out actorFacing,out actorMoving)) { actor.Show(false); continue; }
                    if(!CrowdActor.InView(actorPosition)) { actor.Show(false); continue; }
                    actor.Show(true);
                    actor.Pose(actorPosition, actorFacing, state.Riot, actorMoving, state.Police, animationTime,
                        animationDelta);
                }
            }
            catch (Exception error)
            {
                failed = true;
                foreach (CrowdActor actor in people) if (actor.Root != null) actor.Show(false);
                ShowOfficers(false);
                DevSuppressionReset();
                DevStopAudio();
                Debug.LogError("[CivicPressure] Crowd visuals stopped; event simulation remains active: " + error);
            }
#if DEV
            finally
            {
                if(profileStart!=0) {
                    devProfileTotal+=System.Diagnostics.Stopwatch.GetTimestamp()-profileStart;
                    devProfileMotion+=motionTicks; devProfileFrames++;
                    if(profileVisible) devProfileVisible++;
                    float elapsed=Time.realtimeSinceStartup-devProfileAt;
                    if(elapsed>=5) {
                        double ms=1000d/System.Diagnostics.Stopwatch.Frequency/devProfileFrames;
                        Debug.Log("[CivicPressure] DEV crowd perf: fps="+(devProfileFrames/elapsed).ToString("F1")+
                            " update_ms="+(devProfileTotal*ms).ToString("F2")+" motion_ms="+(devProfileMotion*ms).ToString("F2")+
                            " other_ms="+((devProfileTotal-devProfileMotion)*ms).ToString("F2")+
                            " visible="+devProfileVisible+"/"+devProfileFrames+" actors="+people.Count+" officers="+officers.Count+
                            " (CrowdPreview CPU only; excludes render/GPU/assaults)");
                        devProfileFrames=devProfileVisible=0; devProfileTotal=devProfileMotion=0;
                    }
                }
            }
#endif
        }
        private static bool Visible(Camera camera,Vector3 position)
        {
            if(camera==null)return true;
            Vector3 delta=position-camera.transform.position;
            if(delta.sqrMagnitude>=600*600)return false;
            // Let Unity cull the actual model bounds; a center-point viewport test hides whole crowds.
            return true;
        }
        private void FindSkins()
        {
            scanned = true;
            int count = PrefabCollection<CitizenInfo>.LoadedCount();
            for (uint i = 0; i < count && skins.Count < 12; i++)
            {
                CitizenInfo info = PrefabCollection<CitizenInfo>.GetLoaded(i);
                if (info == null || !(info.m_citizenAI is ResidentAI) || info.m_skinRenderer == null || info.m_skinRenderer.sharedMesh == null || info.m_height < 1.5f || info.m_height > 2.2f) continue;
                if (info.m_agePhase == Citizen.AgePhase.Child || info.m_agePhase == Citizen.AgePhase.Teen0 || info.m_agePhase == Citizen.AgePhase.Teen1) continue;
                Animator animation = info.GetComponent<Animator>();
                if (animation != null && animation.runtimeAnimatorController != null) skins.Add(info);
            }
            Debug.Log("[CivicPressure] Crowd skin pool: " + skins.Count + " resident prefabs; citizen simulation buffers untouched.");
        }
        private void ShowOfficers(bool visible)
        {
            foreach (CrowdActor officer in officers) officer.Show(visible);
        }
        private void Police(UnrestView state, float delta)
        {
            bool visible = state.Police && state.PoliceVehicle != 0;
            if (state.Riot && state.DevPoliceUnits != null) visible = state.Police && state.DevPoliceUnits.Length > 0;
            if (!visible) { ShowOfficers(false); return; }
            if (officers.Count == 0)
            {
                policeSkins.Clear();
                int count = PrefabCollection<CitizenInfo>.LoadedCount();
                for (uint i = 0; i < count && policeSkins.Count < 6; i++)
                {
                    CitizenInfo info = PrefabCollection<CitizenInfo>.GetLoaded(i);
                    if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.PoliceDepartment || info.m_skinRenderer == null) continue;
                    // PoliceCarAI.CreatePolice uses this group; prisoners share the service but use Young0.
                    if (info.m_agePhase != Citizen.AgePhase.Adult0 || info.m_gender != Citizen.Gender.Male || info.m_subCulture != Citizen.SubCulture.Generic) continue;
                    Animator animator = info.GetComponent<Animator>();
                    if (animator != null && animator.runtimeAnimatorController != null) policeSkins.Add(info);
                }
                Debug.Log("[CivicPressure] Police visual skins: " + policeSkins.Count);
            }
            int officerLimit=devPolice.Length;
            for (int budget = 0; budget < 2 && officers.Count < officerLimit; budget++)
                officers.Add(new CrowdActor(transform, policeSkins.Count == 0 ? null : policeSkins[officers.Count % policeSkins.Count], officers.Count, true));
            if (DevSuppressionPolice(delta)) return;
            ShowOfficers(true);
            Vector3 forward = state.Position - state.PolicePosition; forward.y = 0;
            if (forward.sqrMagnitude < .01f) forward = Vector3.forward; else forward.Normalize();
            Vector3 side = new Vector3(forward.z, 0, -forward.x);
            Quaternion facing = Quaternion.LookRotation(forward);
            for (int i = 0; i < officers.Count; i++)
            {
                Vector3 position = state.PolicePosition + forward * 3 + side * ((i - 2.5f) * 1.1f);
                officers[i].Pose(position, facing, false, false, true, animationTime, delta);
            }
        }        public void OnDestroy()
        {
            DevDisposeSuppression();
            DevDisposeAudio();
            DevResetProtest();
            DevResetRiotCrowd();
            foreach (CrowdActor actor in people) actor.Dispose();
            foreach (CrowdActor officer in officers) officer.Dispose();
        }
    }
}
