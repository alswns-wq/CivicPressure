using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using ColossalFramework;
using HarmonyLib;
using UnityEngine;

namespace CivicPressure
{
    internal static class UnrestBootstrap
    {
        // Why the unrest hooks are off, so the panel names the real cause instead of always blaming Harmony.
        internal const string HarmonyMissing = "harmony", Incompatible = "incompatible";
        internal static string Problem;
        // A TM:PE path finder was found but its crowd detour could not be attached. Kept here, away from
        // Harmony-typed members, so code that must run without Harmony can still read it.
        internal static bool TmpeDetourMissing;

        internal static bool Start()
        {
            Problem = null; TmpeDetourMissing = false;
            try
            {
                // Same installer entry used by CitiesHarmony.API.IsHarmonyInstalled; never subscribe automatically.
                Type installer = Type.GetType("CitiesHarmony.Installer, CitiesHarmony", false);
                MethodInfo run = installer == null ? null : installer.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                if (run == null) { Problem = HarmonyMissing; return false; }
                run.Invoke(null, new object[0]);
            }
            catch (Exception error) { Problem = HarmonyMissing; Debug.LogError("[CivicPressure] Harmony installer unavailable: " + error); return false; }
            try { return UnrestPatches.Install(); }
            catch (Exception error) { Problem = Incompatible; Debug.LogError("[CivicPressure] Unrest patches unavailable: " + error); return false; }
        }
    }

    internal static class UnrestPatches
    {
        private const string Id = "CivicPressure.Unrest.v1";
        private static Harmony harmony;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Install()
        {
            if (harmony != null) return true;
            harmony = new Harmony(Id);
            try
            {
                int count = 0;
                foreach (MethodInfo method in typeof(CarAI).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != "CalculateSegmentPosition") continue;
                    int length = method.GetParameters().Length;
                    if (length != 8 && length != 13) continue;
                    harmony.Patch(method, null, new HarmonyMethod(typeof(UnrestPatches).GetMethod("LimitSpeed", BindingFlags.NonPublic | BindingFlags.Static)), null, null);
                    count++;
                }
                if (count != 2) throw new MissingMethodException("Expected two CS1 CarAI segment position overloads");
                InstallRouting();
                PatchResponse(typeof(CitizenMessage), "GetSenderName", Type.EmptyTypes, "OfficeSender");
                PatchResponse(typeof(ChirpPanel),"OnTargetClick",new Type[]{typeof(ColossalFramework.UI.UIComponent),typeof(ColossalFramework.UI.UIMouseEventParameter)},"OfficeClick");
                MethodInfo arrive = typeof(PoliceCarAI).GetMethod("ArriveAtTarget", BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new Type[] { typeof(ushort), typeof(Vehicle).MakeByRefType() }, null);
                if (arrive == null) throw new MissingMethodException("PoliceCarAI.ArriveAtTarget");
                harmony.Patch(arrive, new HarmonyMethod(typeof(UnrestPatches).GetMethod("PoliceArriving", BindingFlags.NonPublic | BindingFlags.Static)), null, null, null);
                MethodInfo release = typeof(VehicleManager).GetMethod("ReleaseVehicle", new Type[] { typeof(ushort) });
                if (release == null) throw new MissingMethodException("VehicleManager.ReleaseVehicle");
                harmony.Patch(release, new HarmonyMethod(typeof(UnrestPatches).GetMethod("VehicleReleasing", BindingFlags.NonPublic | BindingFlags.Static)), null, null, null);
                MethodInfo policePath = typeof(PoliceCarAI).GetMethod("StartPathFind", BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new Type[] { typeof(ushort), typeof(Vehicle).MakeByRefType(), typeof(Vector3), typeof(Vector3), typeof(bool), typeof(bool), typeof(bool) }, null);
                harmony.Patch(policePath, new HarmonyMethod(typeof(UnrestPatches).GetMethod("DirectPolicePath", BindingFlags.NonPublic | BindingFlags.Static)), null, null,
                    new HarmonyMethod(typeof(UnrestPatches).GetMethod("EndPolicePath", BindingFlags.NonPublic | BindingFlags.Static)));
                foreach (MethodInfo create in typeof(PathManager).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (create.Name == "CreatePath") harmony.Patch(create, new HarmonyMethod(typeof(UnrestPatches).GetMethod("PolicePathCategory", BindingFlags.NonPublic | BindingFlags.Static)), null, null, null);
                MethodInfo ambulancePath=typeof(AmbulanceAI).GetMethod("StartPathFind",BindingFlags.NonPublic|BindingFlags.Instance,null,new Type[]{typeof(ushort),typeof(Vehicle).MakeByRefType(),typeof(Vector3),typeof(Vector3),typeof(bool),typeof(bool),typeof(bool)},null);
                harmony.Patch(ambulancePath,new HarmonyMethod(typeof(UnrestPatches).GetMethod("DirectAmbulance",BindingFlags.NonPublic|BindingFlags.Static)),null,null,new HarmonyMethod(typeof(UnrestPatches).GetMethod("EndPolicePath",BindingFlags.NonPublic|BindingFlags.Static)));
                PatchResponse(typeof(AmbulanceAI),"ArriveAtTarget",new Type[]{typeof(ushort),typeof(Vehicle).MakeByRefType()},"MedicalArriving");
                PatchResponse(typeof(AmbulanceAI),"SimulationStep",new Type[]{typeof(ushort),typeof(Vehicle).MakeByRefType(),typeof(Vector3)},"MedicalHolding");
                PatchResponse(typeof(AmbulanceAI),"UpdateBuildingTargetPositions",new Type[]{typeof(ushort),typeof(Vehicle).MakeByRefType(),typeof(Vector3),typeof(ushort),typeof(Vehicle).MakeByRefType(),typeof(int).MakeByRefType(),typeof(float)},"MedicalDestination");
                PatchResponse(typeof(PoliceCarAI), "SimulationStep", new Type[] { typeof(ushort), typeof(Vehicle).MakeByRefType(), typeof(Vector3) }, "HoldPolice");
                PatchResponse(typeof(PoliceCarAI), "UpdateBuildingTargetPositions", new Type[] { typeof(ushort), typeof(Vehicle).MakeByRefType(), typeof(Vector3), typeof(ushort), typeof(Vehicle).MakeByRefType(), typeof(int).MakeByRefType(), typeof(float) }, "SkipBuildingDestination");
                PatchResponse(typeof(PoliceCarAI), "TryCollectCrime", new Type[] { typeof(ushort), typeof(Vehicle).MakeByRefType(), typeof(Vehicle.Frame).MakeByRefType() }, "SkipBuildingCrime");
                PatchResponse(typeof(PoliceCarAI), "CanLeave", new Type[] { typeof(ushort), typeof(Vehicle).MakeByRefType() }, "KeepPoliceOnScene");
                PatchResponse(typeof(PoliceCarAI), "GetLocalizedStatus", new Type[] { typeof(ushort), typeof(Vehicle).MakeByRefType(), typeof(InstanceID).MakeByRefType() }, "PoliceResponseStatus");
                MethodInfo spread = typeof(CommonBuildingAI).GetMethod("HandleFireSpread", BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new Type[] { typeof(ushort), typeof(Building).MakeByRefType(), typeof(int) }, null);
                if (spread == null) throw new MissingMethodException("CommonBuildingAI.HandleFireSpread");
                harmony.Patch(spread, new HarmonyMethod(typeof(UnrestPatches).GetMethod("LimitOwnFireSpread", BindingFlags.NonPublic | BindingFlags.Static)), null, null, null);
                Debug.Log("[CivicPressure] Phase 3 patches installed: two traffic hooks, police arrival, bounded riot fire spread.");
                return true;
            }
            catch { Remove(); throw; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Remove()
        {
            if (harmony == null) return;
            harmony.UnpatchAll(Id); harmony = null;
            TrafficRouting.Reset();
        }

        private static void LimitSpeed(CarAI __instance, PathUnit.Position position, ref Vehicle vehicleData, ref float maxSpeed)
        {
            Closure closure = CivicUnrest.Closure;
            if (closure == null || position.m_segment != closure.Road) return;
            if (DevRiotPassage.Car(closure, vehicleData)) return;
            if (TrafficRouting.Inside(vehicleData, closure.Road)) return;
            if (__instance is PoliceCarAI || (vehicleData.m_flags & Vehicle.Flags.Emergency2) != 0) return;
            VehicleInfo info = vehicleData.Info;
            if (info != null && (info.m_class.m_service == ItemClass.Service.FireDepartment || info.m_class.m_service == ItemClass.Service.HealthCare)) return;
            NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
            if (closure.Road < roads.Length && roads[closure.Road].m_buildIndex == closure.BuildIndex &&
                (roads[closure.Road].m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) == NetSegment.Flags.Created) maxSpeed = 0;
        }

        private static bool OfficeClick(ColossalFramework.UI.UIComponent comp,ColossalFramework.UI.UIMouseEventParameter p)
        {
            if(p.source==null || p.source.parent==null)return true;
            CitizenMessage message=p.source.parent.objectUserData as CitizenMessage;
            string sender;
            if(!CivicChirper.OfficialSender(message,out sender))return true;
            ushort building;uint index;
            if(!CivicChirper.Target(message,out building,out index,out sender))return false;
            if(ToolsModifierControl.cameraController==null || !Singleton<BuildingManager>.exists)return false;
            Building[] buildings=Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            if(building==0 || building>=buildings.Length || !CivicSimulation.Live(buildings[building]) || buildings[building].m_buildIndex!=index)return false;
            InstanceID id=default(InstanceID);id.Building=building;
            ToolsModifierControl.cameraController.SetTarget(id,ToolsModifierControl.cameraController.transform.position,true);
            return false;
        }
        private static bool OfficeSender(CitizenMessage __instance, ref string __result)
        {
            string sender;
            if (!CivicChirper.OfficialSender(__instance, out sender)) return true;
            __result = sender; return false;
        }

        private static void InstallRouting()
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            MethodInfo native = typeof(PathFind).GetMethod("ProcessItemCosts", flags);
            if (native == null) throw new MissingMethodException("PathFind.ProcessItemCosts");
            harmony.Patch(native, new HarmonyMethod(typeof(UnrestPatches).GetMethod("AvoidCrowd", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null);
            harmony.Patch(typeof(PathFind).GetMethod("PathFindImplementation", flags), null,
                new HarmonyMethod(typeof(UnrestPatches).GetMethod("DevPathComplete", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType("TrafficManager.Custom.PathFinding.CustomPathFind", false);
                if (type == null) continue;
                if (TryPatchTmpe(harmony, type)) Debug.Log("[CivicPressure] TMPE crowd detour hook installed.");
                else UnrestBootstrap.TmpeDetourMissing = true;
            }
        }

        // TM:PE is optional. A TM:PE update that renames or reshapes its path cost method only loses the TM:PE
        // detour; protests, police and notices stay enabled with the vanilla hook. This method never throws.
        internal static bool TryPatchTmpe(Harmony target, Type customPathFind)
        {
            if (customPathFind == null) return false;
            MethodInfo costs = null;
            try
            {
                foreach (MethodInfo method in customPathFind.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
                    if (method.Name == "ProcessItemCosts" && (costs == null || method.GetParameters().Length > costs.GetParameters().Length)) costs = method;
                if (costs == null) throw new MissingMethodException("TMPE.ProcessItemCosts");
                target.Patch(costs, new HarmonyMethod(typeof(UnrestPatches).GetMethod("AvoidCrowdTMPE", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null);
                target.Patch(customPathFind.GetMethod("PathFindImplementation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly), null,
                    new HarmonyMethod(typeof(UnrestPatches).GetMethod("DevPathComplete", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
                return true;
            }
            catch (Exception error)
            {
                try { if (costs != null && target != null) target.Unpatch(costs, HarmonyPatchType.Prefix, Id); } catch { }
                // Logging must not turn the optional hook into a failure; Unity logging is unavailable in tests.
                try { Debug.LogWarning("[CivicPressure] TM:PE crowd detour unavailable. Protests and riots stay enabled, but vehicles routed by TM:PE may not detour around crowds. " + error.Message); } catch { }
                return false;
            }
        }
        private static bool AvoidCrowd(ushort segmentID, ref NetSegment segment, bool enableVehicle,
            VehicleInfo.VehicleType ___m_vehicleTypes, VehicleInfo.VehicleCategoryPart1 ___m_vehicleCategoriesPart1,
            VehicleInfo.VehicleCategoryPart2 ___m_vehicleCategoriesPart2,
            uint ___m_calculating,
            ref bool __result)
        {
            if (DevRiotPassage.Allows(CivicUnrest.Closure, segmentID, ___m_calculating,
                VehicleInfo.GetCategory(___m_vehicleCategoriesPart1, ___m_vehicleCategoriesPart2))) return true;
            if (!TrafficRouting.Block(CivicUnrest.Closure, segmentID, ref segment, enableVehicle, ___m_vehicleTypes,
                VehicleInfo.GetCategory(___m_vehicleCategoriesPart1, ___m_vehicleCategoriesPart2))) return true;
            // The return value means "blocked candidate": it enables the native node U-turn branch.
            __result = true; return false;
        }
        private static bool AvoidCrowdTMPE(ushort nextSegmentId, ref NetSegment nextSegment, bool enableVehicle,
            VehicleInfo.VehicleType ___vehicleTypes_, VehicleInfo.VehicleCategory ___vehicleCategory_,
            uint ___calculating_,
            ref bool __result)
        {
            if (DevRiotPassage.Allows(CivicUnrest.Closure, nextSegmentId, ___calculating_, ___vehicleCategory_)) return true;
            if (!TrafficRouting.Block(CivicUnrest.Closure, nextSegmentId, ref nextSegment, enableVehicle, ___vehicleTypes_, ___vehicleCategory_)) return true;
            __result = true; return false;
        }
        private static void DevPathComplete(uint unit) { DevRiotPassage.Completed(unit); }

        private static bool PoliceArriving(ushort vehicleID, ref Vehicle data, ref bool __result)
        {
            if (!PoliceDeployment.Arrive(vehicleID, ref data)) return true;
            __result = false; return false;
        }

        private static void PatchResponse(Type type, string name, Type[] parameters, string prefix)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, parameters, null);
            if (method == null) throw new MissingMethodException(type.Name, name);
            harmony.Patch(method, new HarmonyMethod(typeof(UnrestPatches).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)), null, null, null);
        }

        [ThreadStatic] private static VehicleInfo.VehicleCategory policePathCategory;
        private static void DirectAmbulance(ushort vehicleID, ref Vector3 endPos, ref bool endBothWays, out VehicleInfo.VehicleCategory __state)
        {
            __state=policePathCategory; policePathCategory=VehicleInfo.VehicleCategory.None;
            DevTreatment c=DevCasualties.Find(vehicleID); if(c==null) return;
            endPos=c.Position; endBothWays=true; policePathCategory=VehicleInfo.VehicleCategory.Ambulance;
        }
        private static bool MedicalArriving(ushort vehicleID, ref bool __result)
        {
            if(DevCasualties.Find(vehicleID)==null) return true;
            __result=false; return false;
        }
        private static bool MedicalHolding(ushort vehicleID, ref Vehicle data)
        {
            DevTreatment c=DevCasualties.Find(vehicleID);
            if(c==null || c.Arrived==0) return true;
            PoliceDeployment.Freeze(ref data); return false;
        }
        private static bool MedicalDestination(ushort vehicleID) { return DevCasualties.Find(vehicleID)==null; }
        private static void DirectPolicePath(ushort vehicleID, ref Vehicle vehicleData, ref Vector3 endPos, ref bool endBothWays, ref bool undergroundTarget, out VehicleInfo.VehicleCategory __state)
        {
            __state = policePathCategory;
            policePathCategory = VehicleInfo.VehicleCategory.None;
            if (!PoliceDeployment.Owns(vehicleID)) return;
            policePathCategory = VehicleInfo.VehicleCategory.Police;
            vehicleData.m_flags |= Vehicle.Flags.Emergency2;
            endPos = PoliceDeployment.Target(vehicleID); endBothWays = true; undergroundTarget = false;
        }
        // Native emergency routing widens the category to All. The crowd exclusion then
        // mistakes the convoy for ordinary traffic. Keep its real emergency category while
        // retaining the native emergency speed and ignoreBlocked flags. Thread-local scope
        // ends even if another mod throws; unrelated paths are never exempted.
        private static void PolicePathCategory(ref VehicleInfo.VehicleCategory vehicleCategories)
        { if (TrafficRouting.Emergency(policePathCategory)) vehicleCategories = policePathCategory; }
        private static void EndPolicePath(VehicleInfo.VehicleCategory __state) { policePathCategory = __state; }
        private static bool HoldPolice(ushort vehicleID, ref Vehicle data)
        {
            if (!PoliceDeployment.Holding(vehicleID)) return true;
            PoliceDeployment.Freeze(ref data); return false;
        }
        private static bool SkipBuildingDestination(ushort vehicleID) { return !PoliceDeployment.Owns(vehicleID); }
        private static bool SkipBuildingCrime(ushort vehicleID) { return !PoliceDeployment.Owns(vehicleID); }
        private static bool KeepPoliceOnScene(ushort vehicleID, ref bool __result)
        {
            if (!PoliceDeployment.Holding(vehicleID) && !CivicVisits.Holding(vehicleID)) return true;
            __result = false; return false;
        }
        private static bool PoliceResponseStatus(ushort vehicleID, ref InstanceID target, ref string __result)
        {
            if (!PoliceDeployment.Owns(vehicleID)) return true;
            target = InstanceID.Empty;
            __result = PoliceDeployment.Holding(vehicleID) ? L.T("폭동 군중 대치 · 진압 중") : L.T("폭동 군중 위치로 긴급 출동 중");
            return false;
        }

        private static void VehicleReleasing(ushort __0) { CivicUnrest.VehicleReleased(__0); }

        private static bool LimitOwnFireSpread(ushort buildingID, ref Building buildingData)
        {
            // The first prototype caps damage at its recorded victims. Ordinary city fires retain vanilla spread.
            return !CivicUnrest.OwnFire(buildingID, buildingData.m_buildIndex);
        }
    }
}
