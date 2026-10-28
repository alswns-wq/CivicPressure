using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using ColossalFramework;
using ColossalFramework.UI;
using HarmonyLib;
using UnityEngine;

namespace CivicPressure
{
    // Shared road-vehicle assault. Active attacks are transient and cleaned up on city unload.
    internal static class DevVehicleAssaultRules
    {
        internal static bool AutoAllowed(bool stamp, bool active, bool riot, bool police) { return stamp && active && riot && !police; }
        internal static bool AutoRange(float distanceSquared, float height) { return distanceSquared >= 0 && distanceSquared <= 18 * 18 && Math.Abs(height) <= 4; }
        internal static bool WaitForActors(bool automatic, bool approaching, float seconds, int token, int readyToken)
        { return automatic && !approaching && seconds <= 0 && (token == 0 || token != readyToken); }
        internal static bool ActorsArrived(int count, int arrived) { return count >= 2 && count <= 3 && arrived >= 2 && arrived <= count; }
        internal static bool AtGround(float horizontalSquared, float height)
        { return horizontalSquared >= 0 && horizontalSquared <= .4f * .4f && Math.Abs(height) <= 1; }
        internal static float GripFoot(float height) { return Math.Max(0, height - 1.35f); }
        internal static float GripLean(float time)
        {
            float reach = Progress(time, 0, 2);
            return time < 2 ? 10 + reach * 15 : time < 8 ? 25 + (float)Math.Sin((time - 2) * Math.PI * 8 / 6) * 20 : 25 - Progress(time, 8, 1.5f) * 35;
        }
        internal static float GripCrouch(float time)
        { return time < 2 ? Progress(time, 0, 2) * .12f : time < 8 ? .12f + (float)(1 - Math.Cos((time - 2) * Math.PI * 8 / 6)) * .09f : .12f + Progress(time, 8, 1.5f) * .23f; }
        internal static int Stage(float seconds) { return seconds < 2 ? 0 : seconds < 8 ? 1 : seconds < 9.5f ? 2 : seconds < 12.5f ? 3 : 4; }
        internal static float Progress(float value, float start, float length) { return Math.Max(0, Math.Min(1, (value - start) / length)); }
        internal static float Ease(float value, float start, float length)
        { float p = Progress(value, start, length); return p * p * p * (10 + p * (-15 + 6 * p)); }
        internal static float ClimbReach(float time, int role) { return Ease(time, role * .15f, .45f); }
        internal static float ClimbLift(float time, int role) { return Ease(time, role * .15f + .4f, 1.3f); }
        internal static float Recovery(float time, int role) { return Ease(time, 12.2f + role * .15f, 3.5f - role * .15f); }
        internal static float LandingBounce(float time, int role)
        { return (float)Math.Sin(Progress(time, 10.7f + role * .1f, .5f) * Math.PI) * .1f; }
        internal static float FinalRoll(int outcome, bool left) { return outcome * (left ? -90f : 90f); }
        internal static float FireYaw(float seconds, float roll, float angle)
        { return roll != 0 ? 0 : (angle > 180 ? angle - 360 : angle) * Ease(seconds, 9.5f, 3); }
        internal static int Outcome(int selected, int random) { return selected >= 0 && selected <= 2 ? selected : random; }
        internal static float SlowSpeed(float initial, float seconds)
        { return initial + (.3f - initial) * Progress(seconds, 0, 2); }
        internal static float Clearance(float roll, float width, float height)
        { double radians = roll * Math.PI / 180; return (float)(Math.Abs(Math.Sin(radians)) * width + Math.Abs(Math.Cos(radians)) * height) * .5f; }
        internal static float Roll(float seconds, float finalRoll)
        {
            if (Stage(seconds) == 1)
            {
                double p = Progress(seconds, 2, 6);
                return (float)(Math.Sin(p * Math.PI * 8 + .16 * Math.Sin(p * Math.PI * 6)) * (12 + 8 * p + 2 * Math.Sin(p * Math.PI * 5)));
            }
            float t = Progress(seconds, 9.5f, 3);
            return finalRoll * t * t * (3 - 2 * t);
        }
        internal static float Pitch(float seconds)
        {
            if (Stage(seconds) != 1) return 0;
            double p = Progress(seconds, 2, 6);
            return (float)((Math.Sin(p * Math.PI * 8 + .6) * 6 + Math.Sin(p * Math.PI * 12) * 2) * Math.Sin(p * Math.PI));
        }
        internal static float Rise(float seconds)
        {
            if (Stage(seconds) != 1) return 0;
            double p = Progress(seconds, 2, 6), wave = Math.Sin(p * Math.PI * 8);
            return (float)(wave * wave * .32 * Math.Sin(p * Math.PI));
        }
        internal static float Sway(float seconds)
        {
            if (Stage(seconds) != 1) return 0;
            double p = Progress(seconds, 2, 6);
            return (float)(Math.Sin(p * Math.PI * 6) * .14 * Math.Sin(p * Math.PI));
        }
        internal static float ThrowProgress(float seconds, int actor) { return Progress(seconds, 9.5f, 1.2f + actor * .1f); }
        internal static float ThrowHeight(float seconds, int actor, float height)
        { float p = ThrowProgress(seconds, actor); return height * (1 - p) + (float)Math.Sin(p * Math.PI) * 1.25f; }
        internal static float Slide(float seconds) { float t = Progress(seconds, 9.5f, 3); return 4 * (1 - (1 - t) * (1 - t)); }
        internal static bool RoadVehicle(Type ai)
        { return ai!=null && typeof(CarAI).IsAssignableFrom(ai) && !typeof(AmbulanceAI).IsAssignableFrom(ai); }
        internal static void KeepAssaultMoving(ref Vehicle data)
        {
            // Intentional assault slowdown is not a stuck route. CarAI releases public/service cars
            // at blockCounter 150 (private cars at 100); PassengerCarAI also releases Congestion.
            data.m_blockCounter=0;
            data.m_flags &= ~Vehicle.Flags.Congestion;
        }
        internal static bool Eligible(Vehicle data, bool roadVehicle)
        {
            Vehicle.Flags bad = Vehicle.Flags.Deleted | Vehicle.Flags.WaitingPath | Vehicle.Flags.Underground |
                Vehicle.Flags.Transition | Vehicle.Flags.InsideBuilding | Vehicle.Flags.Parking | Vehicle.Flags.Congestion;
            return roadVehicle && (data.m_flags & (Vehicle.Flags.Created | Vehicle.Flags.Spawned | bad)) ==
                (Vehicle.Flags.Created | Vehicle.Flags.Spawned) && data.m_leadingVehicle == 0 && data.m_trailingVehicle == 0 &&
                data.m_cargoParent == 0 && data.m_firstCargo == 0 && data.m_flags2 == 0;
        }

        // Preflight the ENTIRE manifest before anyone is detached. A broken chain is not an empty car.
        internal static uint[] Manifest(ushort vehicle, uint first, CitizenUnit[] units, Citizen[] citizens, CitizenInstance[] instances)
        {
            int count=ReadManifest(vehicle,first,units,citizens,instances);
            if(count<0) return null;
            if(count==0) return EmptyManifest;
            uint[] result=new uint[count]; Array.Copy(manifestBuffer,result,count); return result;
        }
        private static readonly uint[] EmptyManifest=new uint[0];
        [ThreadStatic] private static uint[] manifestBuffer;
        [ThreadStatic] private static Dictionary<uint,bool> manifestSeen;
        // The simulation thread validates every tick without allocating a passenger list.
        internal static int ReadManifest(ushort vehicle,uint first,CitizenUnit[] units,Citizen[] citizens,CitizenInstance[] instances)
        {
            if(first==0) return 0;
            if(manifestBuffer==null) { manifestBuffer=new uint[1280]; manifestSeen=new Dictionary<uint,bool>(8); }
            manifestSeen.Clear(); int total=0;
            uint unit = first;
            for (int count = 0; unit != 0; count++)
            {
                if (count >= 256 || unit >= units.Length || units[unit].m_vehicle != vehicle ||
                    (units[unit].m_flags & CitizenUnit.Flags.Vehicle) == 0) return -1;
                for (int slot = 0; slot < 5; slot++)
                {
                    uint id = units[unit].GetCitizen(slot);
                    if (id == 0) continue;
                    if (id >= citizens.Length || manifestSeen.ContainsKey(id) || citizens[id].m_vehicle != vehicle ||
                        (citizens[id].m_flags & (Citizen.Flags.Created | Citizen.Flags.Dead)) != Citizen.Flags.Created) return -1;
                    ushort instance = citizens[id].m_instance;
                    if (instance == 0 || instance >= instances.Length || instances[instance].m_citizen != id ||
                        (instances[instance].m_flags & (CitizenInstance.Flags.Created | CitizenInstance.Flags.Deleted |
                        CitizenInstance.Flags.WaitingTransport | CitizenInstance.Flags.EnteringVehicle)) != CitizenInstance.Flags.Created) return -1;
                    manifestSeen.Add(id,true); manifestBuffer[total++]=id;
                }
                unit = units[unit].m_nextUnit;
            }
            return total;
        }
    }

    internal sealed class DevAssaultView
    {
        internal ushort Vehicle;
        internal int Token;
        internal long Riot;
        internal float Seconds, Height, Width, Length, FinalRoll, SpeedLimit;
        internal bool Driving, Approaching, Automatic, Parked, Assembling;
        internal Vector3 Origin;
        internal Vector3 Anchor, Position;
        internal Quaternion Facing;
    }

    internal sealed partial class DevVehicleAssault
    {
        private sealed class Request
        {
            internal ushort Vehicle, Info;
            internal uint Units;
            internal int Outcome;
            internal Vector3 Position;
            internal bool Automatic;
            internal Vector3 Origin;
        }
        private static readonly object Gate = new object();
        private static readonly System.Random Random = new System.Random();
        private static Harmony hooks;
        private static Request pending;
        private static int clearRequested;
        private ushort vehicle, infoIndex;
        private uint units, lastFrame;
        private long riotStamp;
        private Vehicle.Frame original;
        private Vehicle.Flags originalFlags;
        private float seconds, height, width, length, finalRoll, initialSpeed, approachSeconds, stopSeconds, speedLimit, arrivalSeconds;
        private float fireYaw;
        private int assaultToken;
        private volatile int actorsReadyToken;
        private bool driving, approaching, automatic, fromParked, nativeStopped;
        private Vector3 assaultOrigin;
        private static bool ready;
        private static volatile bool visualFailed;
        private volatile DevAssaultView view;
        // At least two attackers per car: 88 participants cannot occupy more than 44 attacks.
        private static readonly DevVehicleAssault[] jobs = CreateJobs();
        private static int nextToken;
        private static DevVehicleAssault[] CreateJobs() { DevVehicleAssault[] result = new DevVehicleAssault[44]; for (int i=0;i<result.Length;i++) result[i]=new DevVehicleAssault(); return result; }
        internal static DevAssaultView At(int slot) { return jobs[slot].view; }
        internal static DevAssaultView View { get { foreach (DevVehicleAssault job in jobs) if(job.view!=null) return job.view; return null; } }
        private static readonly DevVehicleAssault[] vehicleJobs=new DevVehicleAssault[65536];
        private void Track() { if(vehicle!=0) Interlocked.Exchange(ref vehicleJobs[vehicle],this); }
        private static DevVehicleAssault Find(ushort id) { return id==0 ? null : Interlocked.CompareExchange(ref vehicleJobs[id],null,null); }
        private static DevAssaultView FindView(ushort id) { DevVehicleAssault job=Find(id); return job==null ? null : job.view; }
        internal static volatile string Status = "";

        internal static void Install()
        {
            try
            {
                hooks = new Harmony("CivicPressure.DevVehicleAssault");
                patchedMethods.Clear();
                foreach(Type ai in typeof(CarAI).Assembly.GetTypes()) if(DevVehicleAssaultRules.RoadVehicle(ai)) {
                    Patch(ai, "SimulationStep", new Type[] { typeof(ushort), typeof(Vehicle).MakeByRefType(), typeof(Vector3) }, "Hold", false);
                    Patch(ai, "CalculateTargetSpeed", new Type[] { typeof(ushort), typeof(Vehicle).MakeByRefType(), typeof(float), typeof(float) }, "Slow", true);
                    Patch(ai, "GetLocalizedStatus", new Type[] { typeof(ushort), typeof(Vehicle).MakeByRefType(), typeof(InstanceID).MakeByRefType() }, "CarStatus", false);
                }
                Patch(typeof(VehicleManager), "ReleaseVehicle", new Type[] { typeof(ushort) }, "Releasing", false);
                Patch(typeof(Vehicle), "Unspawn", new Type[] { typeof(ushort) }, "Unspawning", false);
                MethodInfo draw = typeof(Vehicle).GetMethod("RenderInstance", BindingFlags.Public | BindingFlags.Static);
                if (draw == null) throw new MissingMethodException("Vehicle.RenderInstance static");
                hooks.Patch(draw, new HarmonyMethod(typeof(DevVehicleAssault).GetMethod("ShakeRender", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null);
                ready = true; visualFailed = false;
                Status = "차량 시험 준비: 구급차를 제외한 도로 차량을 선택하세요.";
            }
            catch (Exception e)
            {
                Remove(); Status = "차량 시험 훅 설치 실패: " + e.Message;
                Debug.LogError("[CivicPressure] DEV vehicle assault unavailable: " + e);
            }
        }
        private static readonly List<MethodInfo> patchedMethods=new List<MethodInfo>();
        internal static MethodInfo PatchTarget(Type type, string name, Type[] parameters)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, parameters, null);
            if (method == null) throw new MissingMethodException(type.Name, name);
            // Harmony requires ReflectedType == DeclaringType; retain overrides, not GetBaseDefinition().
            return method.DeclaringType.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, parameters, null);
        }
        private static void Patch(Type type, string name, Type[] parameters, string callback, bool postfix)
        {
            MethodInfo method = PatchTarget(type, name, parameters);
            if(patchedMethods.Contains(method)) return;
            HarmonyMethod hook = new HarmonyMethod(typeof(DevVehicleAssault).GetMethod(callback, BindingFlags.Static | BindingFlags.NonPublic));
            // Stop the selected wreck before optional traffic-mod prefixes can simulate its movement.
            if (callback == "Hold") hook.priority = Priority.First;
            hooks.Patch(method, postfix ? null : hook, postfix ? hook : null, null, null);
            patchedMethods.Add(method);
        }
        internal static void Remove()
        {
            lock (Gate)
            {
                ready = false; emptyAssaultArea=null; Interlocked.Exchange(ref pending, null); Interlocked.Exchange(ref clearRequested, 0);
                foreach(DevVehicleAssault job in jobs) job.Cleanup("차량 시험 종료");
                Array.Clear(vehicleJobs,0,vehicleJobs.Length);
                ClearWrecks(); autoScanFrame = 0; parkedRiot = 0; parkedHits = 0;
                if (hooks != null) { hooks.UnpatchAll("CivicPressure.DevVehicleAssault"); hooks = null; }
            }
        }
#if DEV
        internal static void RequestSelected(int outcome)
        {
            if (!ready) { Status = "차량 시험을 사용할 수 없습니다. 로그를 확인하세요."; return; }
            if (visualFailed) { Status = "차량 연출 오류로 습격을 중지했습니다. 도시를 다시 불러오세요."; return; }
            ushort id = WorldInfoPanel.AnyWorldInfoPanelOpen() ? WorldInfoPanel.GetCurrentInstanceID().Vehicle : (ushort)0;
            Vehicle[] cars = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            if (id == 0 || id >= cars.Length) { Status = "수동 시험은 도로 차량을 선택하세요. 주차 차량은 실제 폭동에서 자동 선택됩니다."; return; }
            Vehicle car = cars[id];
            Interlocked.Exchange(ref pending, new Request { Vehicle = id, Info = car.m_infoIndex, Units = car.m_citizenUnits, Position = car.GetLastFramePosition(), Outcome = outcome });
            Status = "선택 차량 시험 요청됨: 시뮬레이션 재개 후 실행";
        }
#endif
        internal static void RequestClear() { Interlocked.Exchange(ref clearRequested, 1); }
        internal static void VisualFailure() { visualFailed = true; RequestClear(); }
        internal static void ActorsReady(int token) { foreach(DevVehicleAssault job in jobs) if(job.assaultToken==token) job.actorsReadyToken=token; }
        private void ResetArrival()
        { assaultToken = Interlocked.Increment(ref nextToken); actorsReadyToken = 0; arrivalSeconds = 0; }
        private bool WaitingForActors()
        { return DevVehicleAssaultRules.WaitForActors(automatic, approaching, seconds, assaultToken, actorsReadyToken); }

        internal static void Tick()
        {
            if (!ready) return;
            lock(Gate)
            {
                try {
                if(Interlocked.Exchange(ref clearRequested,0)!=0) {
                    Interlocked.Exchange(ref pending,null);
                    foreach(DevVehicleAssault job in jobs) job.Cleanup("차량 시험 정리");
                    ClearWrecks(); autoScanFrame=Singleton<SimulationManager>.instance.m_currentFrameIndex+600; return;
                }
                long stamp=CivicUnrest.DevRiotStamp; UnrestView riot=CivicUnrest.View;
                if(!riot.Active || riot.Police || pending!=null) emptyAssaultArea=null;
                MaintainWrecks(stamp);
                Request request=Interlocked.Exchange(ref pending,null);
                DevVehicleAssault free=null;
                foreach(DevVehicleAssault job in jobs) if(job.vehicle==0) { free=job; break; }
                if(request!=null && free!=null && Find(request.Vehicle)==null) free.Begin(request,stamp);
                else if(request==null && free!=null) free.AutoBegin(riot,stamp);
                foreach(DevVehicleAssault job in jobs) if(job.vehicle!=0) job.TickOne(riot,stamp);
                } catch(Exception error) {
                    visualFailed=true;
                    foreach(DevVehicleAssault job in jobs) job.Cleanup("동시 습격 오류로 정리");
                    ClearWrecks(); Debug.LogError("[CivicPressure] DEV concurrent assault stopped: "+error);
                }
            }
        }
        private void TickOne(UnrestView riot,long stamp)
        {
            lock(Gate)
            {
                try
                {
                    if (riotStamp != 0 && riotStamp != stamp) { if(!driving && seconds>=12.5f) ArchiveWreck(); else Cleanup("폭동 종료: 공격 정리"); return; }
                    Vehicle[] cars = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
                    if (!Matches(vehicle, cars[vehicle])) { Forget("대상 차량이 변경되어 시험 종료"); return; }
                    if (automatic && driving && approaching && (cars[vehicle].GetLastFramePosition() - assaultOrigin).sqrMagnitude > 24 * 24)
                    { Cleanup("폭동대에서 멀어진 차량의 습격을 취소했습니다."); return; }
                    CitizenManager citizens = Singleton<CitizenManager>.instance;
                    int remaining = DevVehicleAssaultRules.ReadManifest(vehicle, units, citizens.m_units.m_buffer,
                        citizens.m_citizens.m_buffer, citizens.m_instances.m_buffer);
                    if (remaining < 0 || (!driving && remaining != 0))
                    { Cleanup("탑승자 상태가 바뀌어 차량 시험을 중단했습니다."); return; }
                    uint frame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
                    uint delta = unchecked(frame - lastFrame); lastFrame = frame;
                    if (delta > 4096) { Cleanup("시뮬레이션 시계 변경: 시험 정리"); return; }
                    float elapsed = delta / 60f;
                    if(nativeStopped && driving) {
                        // A service route ended during the assault. Keep its existing position
                        // and finish the assault instead of letting native Unspawn hide the car.
                        Vehicle.Frame stopped=cars[vehicle].GetLastFrameData(); stopped.m_velocity=Vector3.zero;
                        cars[vehicle].m_frame0=cars[vehicle].m_frame1=cars[vehicle].m_frame2=cars[vehicle].m_frame3=stopped;
                        approaching=false; speedLimit=0;
                    }
                    if (WaitingForActors())
                    {
                        arrivalSeconds += elapsed;
                        if (arrivalSeconds >= 12) { Debug.Log("[CivicPressure] DEV assault approach timeout: vehicle=" + vehicle); Cleanup("가까운 참가자가 도착하지 않아 습격을 취소했습니다."); return; }
                        elapsed = 0; // The car must not shake before its individual attackers arrive.
                    }
                    if (driving)
                    {
                        float speed = cars[vehicle].GetLastFrameData().m_velocity.magnitude;
                        if (approaching)
                        {
                            approachSeconds += elapsed;
                            speedLimit = DevVehicleAssaultRules.SlowSpeed(initialSpeed, approachSeconds);
                            if (speed <= .45f && approachSeconds >= .35f) { approaching = false; seconds = 0; speedLimit = .3f; }
                            else if (approachSeconds >= 8) { Cleanup("차량이 감속하지 않아 습격을 취소했습니다."); return; }
                        }
                        else
                        {
                            if (seconds < 8) seconds = Math.Min(8, seconds + elapsed);
                            else stopSeconds += elapsed;
                            speedLimit = .3f * (1 - DevVehicleAssaultRules.Progress(stopSeconds, 0, 1));
                            if (seconds >= 8)
                            {
                                if (speed <= .08f)
                                {
                                    if (!Evacuate(ref cars[vehicle])) { Cleanup(Status); return; }
                                }
                                else if (stopSeconds >= 5) { Cleanup("정차하지 못해 전복을 취소했습니다."); return; }
                            }
                        }
                        if (driving)
                        {
                            Vehicle.Frame moving = cars[vehicle].GetLastFrameData();
                            view = new DevAssaultView { Vehicle = vehicle, Token = assaultToken, Riot=riotStamp, Assembling = WaitingForActors(), Seconds = Math.Min(seconds, 7.999f), Height = height, Width = width, Length = length, Automatic = automatic, Origin = assaultOrigin,
                                Anchor = moving.m_position, Facing = moving.m_rotation, Position = moving.m_position,
                                Driving = true, Approaching = approaching, FinalRoll = finalRoll, SpeedLimit = speedLimit };
                            Status = "차량 " + vehicle + " · " + (approaching ? "서서히 감속 중" : WaitingForActors() ? "가까운 참가자 접근 중" : seconds >= 8 ? "전복 전 정차·하차 대기" : "서행 중 습격 · " + StageText(seconds, finalRoll));
                            return;
                        }
                    }
                    else seconds += elapsed;
                    SetPose(ref cars[vehicle]);
                    view = new DevAssaultView { Vehicle = vehicle, Token = assaultToken, Riot=riotStamp, Assembling = WaitingForActors(), Seconds = seconds, Height = height, Width = width, Length = length, Automatic = automatic, Origin = assaultOrigin, Parked = fromParked,
                        Anchor = original.m_position, Facing = original.m_rotation, Position = cars[vehicle].GetLastFramePosition(), FinalRoll = finalRoll };
                    Status = (fromParked ? "주차 차량 " : "차량 ") + vehicle + " · " + (WaitingForActors() ? "가까운 참가자 접근 중" : StageText(seconds, finalRoll)) + (fromParked ? " · 주차 피해 " + parkedHits : " · 탑승자 하차 완료") + " · " + (riotStamp == 0 ? "수동 정리 시 잔해 제거" : "폭동 종료 시 잔해 제거");
                    if (automatic && seconds >= 18) ArchiveWreck();
                }
                catch (Exception e)
                {
                    Debug.LogError("[CivicPressure] DEV vehicle assault stopped: " + e);
                    visualFailed = true; Cleanup("차량 시험 오류로 정리: " + e.Message); ClearWrecks();
                }
            }
        }

        private void Begin(Request request, long stamp)
        {
            Vehicle[] cars = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            if (request.Vehicle == 0 || request.Vehicle >= cars.Length) return;
            Vehicle car = cars[request.Vehicle]; VehicleInfo info = car.Info;
            if (car.m_infoIndex != request.Info || car.m_citizenUnits != request.Units ||
                (car.GetLastFramePosition() - request.Position).sqrMagnitude > 128 * 128 ||
                !DevVehicleAssaultRules.Eligible(car, info != null && info.m_vehicleAI!=null && DevVehicleAssaultRules.RoadVehicle(info.m_vehicleAI.GetType())))
            { Status = "대상 제외: 구급차·연결 차량·지하·주차 중 차량은 제외합니다."; return; }
            CitizenManager citizens = Singleton<CitizenManager>.instance;
            uint[] manifest = DevVehicleAssaultRules.Manifest(request.Vehicle, car.m_citizenUnits,
                citizens.m_units.m_buffer, citizens.m_citizens.m_buffer, citizens.m_instances.m_buffer);
            if (manifest == null) { Status = "탑승자 연결을 검증하지 못해 습격을 보류했습니다."; return; }
            foreach (uint person in manifest)
                if (citizens.m_instances.m_buffer[citizens.m_citizens.m_buffer[person].m_instance].Info == null)
                { Status = "탑승자 이동 정보를 확인하지 못해 습격을 보류했습니다."; return; }

            vehicle = request.Vehicle; infoIndex = car.m_infoIndex; units = car.m_citizenUnits;
            height = Math.Max(.8f, Math.Min(3, info.m_generatedInfo.m_size.y));
            width = Math.Max(1, Math.Min(4, info.m_generatedInfo.m_size.x));
            length = Math.Max(2, Math.Min(20, info.m_generatedInfo.m_size.z)); ResetArrival();
            finalRoll = DevVehicleAssaultRules.FinalRoll(DevVehicleAssaultRules.Outcome(request.Outcome, Random.Next(3)), Random.Next(2) == 0);
            fireYaw = finalRoll == 0 ? (float)(Random.NextDouble() * 360) : 0;
            initialSpeed = car.GetLastFrameData().m_velocity.magnitude;
            driving = approaching = true; nativeStopped=false; approachSeconds = stopSeconds = 0; speedLimit = initialSpeed;
            seconds = 0; riotStamp = stamp; lastFrame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
            automatic = request.Automatic; assaultOrigin = request.Origin; fromParked = false;
            Track();
            Debug.Log("[CivicPressure] DEV assault begin: vehicle="+vehicle+", ai="+info.m_vehicleAI.GetType().Name+", blocked="+car.m_blockCounter+", prefab="+info.name);
            DevVehicleAssaultRules.KeepAssaultMoving(ref cars[vehicle]);
        }
        private bool Evacuate(ref Vehicle car)
        {
            CitizenManager citizens = Singleton<CitizenManager>.instance;
            uint[] manifest = DevVehicleAssaultRules.Manifest(vehicle, units,
                citizens.m_units.m_buffer, citizens.m_citizens.m_buffer, citizens.m_instances.m_buffer);
            if (manifest == null) { Status = "하차 전 탑승자 연결 검증 실패로 습격 취소"; return false; }
            foreach (uint person in manifest)
                if (citizens.m_instances.m_buffer[citizens.m_citizens.m_buffer[person].m_instance].Info == null)
                { Status = "하차 전 시민 이동 정보 검증 실패로 습격 취소"; return false; }
            original = car.GetLastFrameData(); originalFlags = car.m_flags;
            foreach (uint person in manifest)
            {
                ushort instance = citizens.m_citizens.m_buffer[person].m_instance;
                citizens.m_instances.m_buffer[instance].Info.m_citizenAI.SetCurrentVehicle(instance,
                    ref citizens.m_instances.m_buffer[instance], 0, 0, original.m_position);
            }
            foreach (uint person in manifest)
                if (citizens.m_citizens.m_buffer[person].m_vehicle != 0)
                { Status = "탑승자가 차량에서 벗어나지 못해 습격을 취소했습니다."; return false; }
            // Never turn a partial evacuation into a wreck or delete its remaining occupants.
            uint[] remaining = DevVehicleAssaultRules.Manifest(vehicle, units, citizens.m_units.m_buffer,
                citizens.m_citizens.m_buffer, citizens.m_instances.m_buffer);
            if (remaining == null || remaining.Length != 0) { Status = "하차가 완료되지 않아 습격을 취소했습니다."; return false; }
            driving = false; seconds = 8;
            if (car.m_path != 0) { Singleton<PathManager>.instance.ReleasePath(car.m_path); car.m_path = 0; }
            car.m_flags &= ~(Vehicle.Flags.WaitingPath | Vehicle.Flags.Arriving);
            // Keep native route/depot ownership until ReleaseVehicle performs the matching AI cleanup.
            // PassengerCarAI also uses Congestion for cleanup; other road AIs have their own lifecycle.
            car.m_flags |= Vehicle.Flags.Stopped | Vehicle.Flags.Congestion;
            Debug.Log("[CivicPressure] DEV assault: vehicle=" + vehicle + ", evacuated=" + manifest.Length + ", roll=" + finalRoll + ", prefab=" + car.Info.name);
            return true;
        }
        private bool Matches(ushort id, Vehicle data)
        {
            return vehicle != 0 && id == vehicle && data.m_infoIndex == infoIndex && data.m_citizenUnits == units &&
                (data.m_flags & (Vehicle.Flags.Created | Vehicle.Flags.Deleted | Vehicle.Flags.Spawned)) == (Vehicle.Flags.Created | Vehicle.Flags.Spawned);
        }
        private void SetPose(ref Vehicle car)
        {
            float roll = DevVehicleAssaultRules.Roll(seconds, finalRoll);
            Quaternion facing = original.m_rotation * Quaternion.Euler(fromParked && seconds < 8 ? DevVehicleAssaultRules.Pitch(seconds) : 0, DevVehicleAssaultRules.FireYaw(seconds, finalRoll, fireYaw), roll);
            float jump = (float)Math.Sin(DevVehicleAssaultRules.Progress(seconds, 9.5f, 3) * Math.PI) * (finalRoll == 0 ? .1f : .65f);
            Vector3 pivot = Vector3.up * (height * .5f);
            Vector3 position = original.m_position + original.m_rotation * Vector3.forward * DevVehicleAssaultRules.Slide(seconds) +
                Vector3.up * DevVehicleAssaultRules.Clearance(roll, width, height) - facing * pivot + Vector3.up * jump;
            if (fromParked && seconds < 8) position += original.m_rotation * new Vector3(DevVehicleAssaultRules.Sway(seconds), DevVehicleAssaultRules.Rise(seconds), 0);
            VehicleManager manager = Singleton<VehicleManager>.instance;
            manager.RemoveFromGrid(vehicle, ref car, car.Info.m_isLargeVehicle);
            Vehicle.Frame frame = original; frame.m_position = position; frame.m_rotation = facing;
            frame.m_velocity = Vector3.zero; frame.m_swayPosition = frame.m_swayVelocity = Vector3.zero;
            car.m_frame0 = car.m_frame1 = car.m_frame2 = car.m_frame3 = frame;
            car.m_flags |= Vehicle.Flags.Stopped;
            manager.AddToGrid(vehicle, ref car, car.Info.m_isLargeVehicle);
            car.Info.m_vehicleAI.FrameDataUpdated(vehicle, ref car, ref car.m_frame0);
        }
        private void Cleanup(string reason)
        {
            // The native path/driver still own a car being slowed or shaken. Never snap it back or delete it.
            if (driving) { Forget(reason); return; }
            ushort id = vehicle;
            try
            {
                if (id != 0 && Singleton<VehicleManager>.exists)
                {
                    Vehicle[] cars = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
                    if (Matches(id, cars[id]))
                    {
                        CitizenManager citizens = Singleton<CitizenManager>.instance;
                        uint[] occupants = DevVehicleAssaultRules.Manifest(id, units, citizens.m_units.m_buffer,
                            citizens.m_citizens.m_buffer, citizens.m_instances.m_buffer);
                        if (occupants != null && occupants.Length == 0)
                        {
                            RestoreUnattackedParked(cars[id].Info);
                            Forget(reason); // Release hook cannot confuse this ID with a new allocation.
                            Singleton<VehicleManager>.instance.ReleaseVehicle(id);
                            return;
                        }
                        // No destructive cleanup when passenger validation failed. Restore native movement.
                        float previous = seconds; seconds = 0; SetPose(ref cars[id]); seconds = previous;
                        cars[id].m_flags = originalFlags;
                        reason += " · 탑승자가 남아 차량을 삭제하지 않고 일반 운행으로 반환";
                    }
                }
            }
            catch (Exception e) { Debug.LogError("[CivicPressure] DEV wreck cleanup failed; native empty-car cleanup remains available: " + e); }
            finally { Forget(reason); }
        }
        private void Forget(string reason) { ReleaseRoadBudget(); if(vehicle!=0) Interlocked.CompareExchange(ref vehicleJobs[vehicle],null,this); vehicle = 0; units = 0; riotStamp = 0; seconds = 0; driving = approaching = automatic = fromParked = nativeStopped = false; view = null; Status = reason; }
        private static bool Unspawning(ref Vehicle __instance,ushort __0)
        {
            DevVehicleAssault job=Find(__0);
            if(job==null) return true;
            lock(Gate) {
                if(!job.Matches(__0,__instance)) return true;
                job.nativeStopped=true;
                return false;
            }
        }
        private static bool Hold(ushort __0, ref Vehicle __1)
        {
            if(Find(__0)==null && (wreckIds==null || __0>=wreckIds.Length || !wreckIds[__0])) return true;
            lock(Gate) {
                DevVehicleAssault job=Find(__0);
                if(job==null) return !HeldWreck(__0,__1);
                if(!job.Matches(__0,__1)) return true;
                if(!job.driving) return false;
                if(job.nativeStopped) return false;
                // This prefix runs first, before traffic-mod and native timeout checks.
                // Continue normal driving/path/passenger simulation until evacuation succeeds.
                DevVehicleAssaultRules.KeepAssaultMoving(ref __1);
                return true;
            }
        }
        private static void Slow(ushort __0, ref float __result)
        {
            DevAssaultView current = FindView(__0);
            if (current != null && current.Vehicle == __0 && current.Driving) __result = Math.Min(__result, current.SpeedLimit);
        }
        private static void ShakeRender(InstanceID id, ref Vector3 position, ref Quaternion rotation)
        {
            DevAssaultView current = FindView(id.Vehicle);
            if (current != null && current.Vehicle == id.Vehicle && current.Driving && !current.Approaching)
            {
                position += rotation * new Vector3(DevVehicleAssaultRules.Sway(current.Seconds), DevVehicleAssaultRules.Rise(current.Seconds), 0);
                rotation *= Quaternion.Euler(DevVehicleAssaultRules.Pitch(current.Seconds), 0, DevVehicleAssaultRules.Roll(current.Seconds, current.FinalRoll));
            }
        }
        private static bool CarStatus(ushort __0, ref Vehicle __1, ref InstanceID __2, ref string __result)
        {
            DevAssaultView current = FindView(__0);
            if (current == null || current.Vehicle != __0) return WreckStatus(__0, __1, ref __result);
            __2 = default(InstanceID); __result = current.Approaching ? L.T("습격 전 감속 중") : current.Assembling ? L.T("참가자 접근 중") : StageText(current.Seconds, current.FinalRoll); return false;
        }
        private static string StageText(float time, float roll)
        {
            switch (DevVehicleAssaultRules.Stage(time))
            {
                case 0: return L.T("차량 습격 · 올라타기");
                case 1: return L.T("차량 습격 · 흔들기 (4회)");
                case 2: return L.T("차량 습격 · 참가자 뛰어내리기");
                case 3: return roll == 0 ? L.T("미끄러지는 차량 · 폭동 피해") : L.T("미끄러지는 전복 차량 · 폭동 피해");
                default: return roll == 0 ? L.T("불타는 차량 · 폭동 피해") : Math.Abs(roll) == 90 ? L.T("옆으로 넘어져 불타는 차량 · 폭동 피해") : L.T("불타는 전복 차량 · 폭동 피해");
            }
        }
        private static void Releasing(ushort __0)
        {
            Request queued=pending;
            if(Find(__0)==null && (wreckIds==null || __0>=wreckIds.Length || !wreckIds[__0]) && (queued==null || queued.Vehicle!=__0)) return;
            lock (Gate)
            {
                Request request = pending;
                if (request != null && request.Vehicle == __0) Interlocked.CompareExchange(ref pending, null, request);
                DevVehicleAssault job=Find(__0); if(job!=null) {
                    string reason="대상 차량이 외부에서 제거되어 시험 종료 · 차량 "+__0+" · "+(job.driving ? "감속/습격 중" : "전복 후")+" · "+job.seconds.ToString("0.0")+"s";
                    if(ready) LogExternalRelease(__0,job);
                    job.Forget(reason);
                }
                RemoveWreck(__0);
            }
        }
        private static void LogExternalRelease(ushort id,DevVehicleAssault job)
        {
            if(!Singleton<VehicleManager>.exists) return;
            Vehicle car=Singleton<VehicleManager>.instance.m_vehicles.m_buffer[id];
            Debug.LogWarning("[CivicPressure] DEV assault external release: vehicle="+id+", driving="+job.driving+", seconds="+job.seconds+", blocked="+car.m_blockCounter+", flags="+car.m_flags+", prefab="+(car.Info==null ? "unknown" : car.Info.name));
        }
    }

    internal sealed class DevAssaultActors : MonoBehaviour
    {
        private readonly List<CrowdActor> actors = new List<CrowdActor>();
        private bool failed;
        internal int Slot;
        private readonly bool[] childrenCreated = new bool[44];
        private readonly bool[] counted = new bool[3];
        private readonly DevVisualMotion motion = new DevVisualMotion();
        private int lastToken;
        private float approachTime;
        private CrowdPreview crowd;
        private DevRoadRibbon attackRoad;
        private readonly CrowdActor[] participants = new CrowdActor[3];
        private readonly int[] participantIndices = { -1, -1, -1 };
        private readonly float[] approachSpeeds = new float[3];
        private readonly bool[] participantReady = new bool[3];
        private readonly float[] joinedAt = new float[3];
        private readonly Dictionary<ushort, DevAssaultFire> fires = new Dictionary<ushort, DevAssaultFire>();
        private readonly List<ushort> liveFires = new List<ushort>(), removeFires = new List<ushort>();
        // CrowdPreview updates first, so borrowed models are posed exactly once at their real position.
        public void LateUpdate()
        {
            if(Slot==0) for(int i=1;i<44;i++) if(!childrenCreated[i] && DevVehicleAssault.At(i)!=null) { DevAssaultActors child=gameObject.AddComponent<DevAssaultActors>(); child.Slot=i; childrenCreated[i]=true; }
            DevAssaultView state = DevVehicleAssault.At(Slot);
            if(state!=null && state.Automatic && (!CivicUnrest.View.Active || !CivicUnrest.View.Riot || state.Riot!=CivicUnrest.DevRiotStamp)) state=null;
            if (failed) return;
            try
            {
                if(Slot==0) RefreshFires(state);
                if (state == null || state.Seconds >= 18)
                { ReleaseParticipants(); foreach (CrowdActor actor in actors) actor.Show(false); lastToken = 0; motion.Reset(); return; }
                if (lastToken != state.Token)
                { ReleaseParticipants(); motion.Reset(); approachTime = 0; lastToken = state.Token; attackRoad = CivicUnrest.View.DevRoad; }
                if (!state.Automatic && actors.Count == 0)
                {
                    CitizenInfo skin = null;
                    for (uint i = 0; i < PrefabCollection<CitizenInfo>.LoadedCount(); i++)
                    {
                        CitizenInfo candidate = PrefabCollection<CitizenInfo>.GetLoaded(i);
                        if (candidate != null && candidate.m_citizenAI is ResidentAI && candidate.m_height >= 1.5f && candidate.m_height <= 2.2f)
                        { skin = candidate; break; }
                    }
                    for (int i = 0; i < 3; i++) actors.Add(new CrowdActor(transform, skin, i));
                }
                SimulationManager simulation = Singleton<SimulationManager>.instance;
                bool paused = simulation.SimulationPaused || simulation.ForcedSimulationPaused;
                float time = (float)motion.Follow(state.Seconds, Time.unscaledDeltaTime, paused);
                float delta = DevVisualMotion.FrameDelta(simulation.m_simulationTimeDelta, paused);
                Vector3 anchor = state.Anchor; Quaternion carFacing = state.Facing;
                if (state.Driving || (state.Parked && time < 8))
                    Singleton<VehicleManager>.instance.m_vehicles.m_buffer[state.Vehicle].GetSmoothPosition(state.Vehicle, out anchor, out carFacing);
                if (state.Automatic)
                {
                    foreach (CrowdActor actor in actors) actor.Show(false);
                    if (crowd == null) crowd = GetComponent<CrowdPreview>();
                    approachTime += delta;
                    if (state.Assembling || state.Approaching)
                    {
                        int count = 0, arrived = 0;
                        for (int i = 0; i < participants.Length; i++)
                        {
                            Vector3 ground = Ground(state, i, anchor, carFacing);
                            if (participants[i] == null && crowd != null)
                                participants[i] = crowd.DevBorrowRiotActor(anchor + carFacing * ground, out participantIndices[i]);
                            if (participants[i] == null) continue;
                            count++;
                            participantReady[i] = Approach(participants[i], state, anchor, carFacing, ground, i, delta);
                            if (participantReady[i]) arrived++;
                        }
                        if (!paused && DevVehicleAssaultRules.ActorsArrived(count, arrived)) DevVehicleAssault.ActorsReady(state.Token);
                        return;
                    }
                }
                for (int i = 0; i < 3; i++)
                {
                    CrowdActor actor = state.Automatic ? participants[i] : actors[i];
                    if (actor == null) continue;
                    if (state.Automatic && !participantReady[i])
                    {
                        if (time > 4) { ReleaseParticipant(i); continue; }
                        if (!Approach(actor, state, anchor, carFacing, Ground(state, i, anchor, carFacing), i, delta)) continue;
                        participantReady[i] = true; joinedAt[i] = time;
                    }
                    if(state.Automatic && !counted[i] && time>=2 && crowd!=null) { crowd.DevCountAttack(participantIndices[i]); counted[i]=true; }
                    float side = i == 0 ? -1 : 1;
                    bool roofRider = state.Automatic && i == 2;
                    float footHeight = roofRider ? state.Height : DevVehicleAssaultRules.GripFoot(state.Height);
                    Vector3 ground = Ground(state, i, anchor, carFacing);
                    Vector3 roof = roofRider ? new Vector3(.2f, footHeight, -state.Length * .15f) : new Vector3(side * (state.Width * .5f + .12f), footHeight, (i - 1) * .8f);
                    float climbTime = time - joinedAt[i];
                    float climb = DevVehicleAssaultRules.Ease(climbTime, i * .15f, 1.4f);
                    Vector3 local = i == 2 && !roofRider ? Vector3.Lerp(ground, new Vector3(ground.x * .65f, 0, ground.z), climb) : Vector3.Lerp(ground, roof, climb);
                    if (i != 2 || roofRider) local.y = Mathf.Lerp(ground.y, footHeight, DevVehicleAssaultRules.ClimbLift(climbTime, i));
                    bool thrown = (i != 2 || roofRider) && time >= 9.5f;
                    float tumble = 0, bank = 0;
                    if (thrown)
                    {
                        float flight = DevVehicleAssaultRules.ThrowProgress(time, i);
                        float standing = DevVehicleAssaultRules.Recovery(time, i);
                        Vector3 landing = new Vector3(side * (3.3f + i * .4f), 0, (i - 1) * 1.1f + 2);
                        float drift = DevVehicleAssaultRules.Ease(flight, 0, 1);
                        local = Vector3.Lerp(roof, landing, drift);
                        local.y = DevVehicleAssaultRules.ThrowHeight(time, i, footHeight - .35f) + .16f * flight * (1 - standing) + DevVehicleAssaultRules.LandingBounce(time, i);
                        tumble = 90 * flight * (1 - standing);
                        bank = side * 40 * (float)Math.Sin(flight * Math.PI);
                        if (time >= 16) { local.y = 0; local += new Vector3(side, 0, 1) * ((time - 16) * 1.5f); }
                    }
                    else if (i == 2 && time >= 9.5f)
                        local += new Vector3(side, 0, 1) * Math.Min(6, (time - 9.5f) * 1.2f);
                    if ((i != 2 || roofRider) && state.Driving)
                    {
                        local = Quaternion.Euler(DevVehicleAssaultRules.Pitch(time), 0, DevVehicleAssaultRules.Roll(time, state.FinalRoll)) * local;
                        local += new Vector3(DevVehicleAssaultRules.Sway(time), DevVehicleAssaultRules.Rise(time), 0);
                    }
                    Quaternion facing = roofRider ? carFacing : carFacing * Quaternion.Euler(0, side < 0 ? 90 : -90, 0);
                    if (state.Automatic && !thrown) facing = Quaternion.Slerp(actor.Root.transform.rotation, facing, DevVisualMotion.Blend(delta, 8));
                    bool moving = time < 2 || (i == 2 && !roofRider && (state.Driving || time >= 9.5f)) || time >= 16;
                    Vector3 actorPosition = anchor + carFacing * local;
                    DevRoadRibbon ribbon = attackRoad;
                    if (state.Automatic && ribbon != null)
                    {
                        Vector3 onRoad = ribbon.Constrain(actorPosition);
                        onRoad.y = actorPosition.y; actorPosition = onRoad;
                    }
                    if(!CrowdActor.InView(actorPosition)) {
                        actor.Root.transform.position=actorPosition; actor.Root.transform.rotation=facing;
                        actor.Show(false); continue;
                    }
                    actor.Show(true); actor.Pose(actorPosition, facing, true, moving, thrown, time, delta);
                    if (thrown) { actor.Root.transform.rotation = facing * Quaternion.Euler(tumble, 0, bank); actor.VehicleFall(facing, time, i); }
                    else if (time < 9.5f)
                    {
                        Vector3 grip = new Vector3(side * state.Width * .45f, i == 2 ? state.Height * .65f : state.Height, (i - 1) * .8f);
                        if (roofRider) grip = roof + new Vector3(0, .65f, .45f);
                        if (state.Driving)
                        {
                            grip = Quaternion.Euler(DevVehicleAssaultRules.Pitch(time), 0, DevVehicleAssaultRules.Roll(time, state.FinalRoll)) * grip;
                            grip += new Vector3(DevVehicleAssaultRules.Sway(time), DevVehicleAssaultRules.Rise(time), 0);
                        }
                        if (climbTime < 2)
                            actor.ClimbVehicle(facing, DevVehicleAssaultRules.ClimbReach(climbTime, i), DevVehicleAssaultRules.ClimbLift(climbTime, i),
                                anchor + carFacing * grip, anchor + carFacing * ground);
                        else actor.GripVehicle(facing, time, anchor + carFacing * grip);
                    }
                    actor.HidePlacard();
                }
            }
            catch (Exception e)
            {
                ReleaseParticipants();
                failed = true; foreach (DevAssaultFire fire in fires.Values) fire.Dispose(); fires.Clear(); foreach (CrowdActor actor in actors) actor.Show(false);
                DevVehicleAssault.VisualFailure();
                Debug.LogError("[CivicPressure] DEV assault actors failed: " + e);
            }
        }
        private Vector3 Ground(DevAssaultView state, int role, Vector3 anchor, Quaternion facing)
        {
            Vector3 ground = state.Automatic && role == 2 ? new Vector3(.2f, 0, -state.Length * .5f - .8f) :
                new Vector3((role == 0 ? -1 : 1) * (state.Width * .5f + (state.Automatic ? .8f : 1.5f)), 0, (role - 1) * 1.1f);
            DevRoadRibbon ribbon = attackRoad;
            if (state.Automatic && ribbon != null)
                ground = Quaternion.Inverse(facing) * (ribbon.Constrain(anchor + facing * ground) - anchor);
            return ground;
        }
        private bool Approach(CrowdActor actor, DevAssaultView state, Vector3 anchor, Quaternion carFacing, Vector3 ground, int role, float delta)
        {
            Transform body = actor.Root.transform;
            Vector3 local = Quaternion.Inverse(carFacing) * (body.position - anchor), waypoint = ground;
            float halfWidth = state.Width * .5f + .45f, halfLength = state.Length * .5f + .5f;
            // ponytail: a rear/front corner detour avoids this car, not arbitrary street obstacles.
            DevRiotCrowdRules.ApproachWaypoint(local.x, local.z, ground.x, ground.z, halfWidth, halfLength, role == 2, out waypoint.x, out waypoint.z);
            Vector3 target = anchor + carFacing * waypoint;
            float desiredSpeed = Math.Min(2.8f, Math.Max(state.Driving ? 1.5f : .15f, (target - body.position).magnitude * 3));
            if (approachTime < role * .4f) desiredSpeed = 0;
            approachSpeeds[role] = Mathf.MoveTowards(approachSpeeds[role], desiredSpeed, delta * 6);
            Vector3 position = Vector3.MoveTowards(body.position, target, delta * approachSpeeds[role]);
            DevRoadRibbon ribbon = attackRoad;
            if (state.Automatic && ribbon != null) position = ribbon.Constrain(position);
            Vector3 travel = position - body.position; travel.y = 0;
            Vector3 toward = anchor - position; toward.y = 0;
            bool moving = delta > 0 && travel.sqrMagnitude > .0025f * delta * delta;
            Quaternion look = moving ? Quaternion.LookRotation(travel) : toward.sqrMagnitude > .01f ? Quaternion.LookRotation(toward) : body.rotation;
            Quaternion turn=Quaternion.Slerp(body.rotation,look,DevVisualMotion.Blend(delta,8));
            if(CrowdActor.InView(position)) {
                actor.Show(true); actor.Pose(position, turn, true, moving, false, approachTime, delta * Math.Min(1, approachSpeeds[role] / 2.8f));
            } else { body.position=position; body.rotation=turn; actor.Show(false); }
            actor.HidePlacard();
            Vector3 remaining = position - (anchor + carFacing * ground);
            return approachTime >= role * .4f && DevVehicleAssaultRules.AtGround(remaining.x * remaining.x + remaining.z * remaining.z, remaining.y);
        }
        private void ReleaseParticipant(int i)
        {
            if (participants[i] != null && crowd != null) crowd.DevReturnRiotActor(participantIndices[i]);
            participants[i] = null; participantIndices[i] = -1; approachSpeeds[i] = joinedAt[i] = 0; participantReady[i] = false; counted[i]=false;
        }
        private void ReleaseParticipants()
        {
            for (int i = 0; i < participants.Length; i++)
            {
                ReleaseParticipant(i);
            }
        }
        private void RefreshFires(DevAssaultView current)
        {
            liveFires.Clear(); removeFires.Clear();
            for(int i=0;i<44;i++) { DevAssaultView active=DevVehicleAssault.At(i); if(active!=null) ShowFire(active); }
            foreach (DevAssaultView wreck in DevVehicleAssault.WreckViews) ShowFire(wreck);
            foreach (ushort id in fires.Keys) if (!liveFires.Contains(id)) removeFires.Add(id);
            foreach (ushort id in removeFires) { fires[id].Dispose(); fires.Remove(id); }
        }
        private void ShowFire(DevAssaultView state)
        {
            if (liveFires.Count >= 8 || DevVehicleAssaultRules.Stage(state.Seconds) != 4) return;
            Camera camera = Camera.main;
            if (camera != null)
            {
                Vector3 p = camera.WorldToViewportPoint(state.Position);
                if ((state.Position - camera.transform.position).sqrMagnitude > 600 * 600 || !CrowdVisualRules.InViewport(p.x, p.y, p.z, .2f)) return;
            }
            DevAssaultFire fire;
            if (!fires.TryGetValue(state.Vehicle, out fire)) { fire = new DevAssaultFire(transform); fires.Add(state.Vehicle, fire); }
            fire.Update(state); liveFires.Add(state.Vehicle);
        }
        public void OnDestroy()
        {
            ReleaseParticipants();
            foreach (DevAssaultFire fire in fires.Values) fire.Dispose(); fires.Clear();
            foreach (CrowdActor actor in actors) actor.Dispose(); actors.Clear();
        }
    }
    internal sealed class DevAssaultFire
    {
        private readonly Transform parent;
        private GameObject fireRoot;
        private ParticleSystem[] fireSystems;
        internal DevAssaultFire(Transform root) { parent = root; }
        internal void Update(DevAssaultView state)
        {
            if (fireRoot == null)
            {
                FireEffect effect = Singleton<BuildingManager>.instance.m_properties.m_fireEffect as FireEffect;
                if (effect == null || effect.m_particleEffect == null) throw new InvalidOperationException("Missing native fire particle template");
                // FireEffect magnitude only changes emission probability; its shared particle size is building-sized.
                // Scale a visual-only clone instead, leaving every native building fire untouched.
                fireRoot = new GameObject("CivicPressure_VehicleFire"); fireRoot.SetActive(false);
                fireRoot.transform.parent = parent; fireRoot.transform.localScale = Vector3.one * .60f;
                GameObject clone = UnityEngine.Object.Instantiate(effect.m_particleEffect.gameObject, fireRoot.transform, false);
                clone.transform.localPosition = Vector3.zero; clone.transform.localRotation = Quaternion.identity; clone.transform.localScale = Vector3.one;
                foreach (MonoBehaviour behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(behaviour);
                fireSystems = clone.GetComponentsInChildren<ParticleSystem>(true);
                foreach (ParticleSystem system in fireSystems)
                {
                    system.Stop(false); system.Clear(false);
                    ParticleSystem.MainModule main = system.main;
                    main.loop = true; main.playOnAwake = false;
                    main.scalingMode = ParticleSystemScalingMode.Hierarchy; main.simulationSpace = ParticleSystemSimulationSpace.Local;
                    main.startLifetimeMultiplier *= .65f; main.maxParticles = Math.Min(128, main.maxParticles);
                }
                ParticleSystem root = clone.GetComponent<ParticleSystem>();
                if (root == null) throw new InvalidOperationException("Missing native fire particle system");
                ParticleSystem.MainModule rootMain = root.main;
                rootMain.startSpeed = new ParticleSystem.MinMaxCurve(effect.m_particleEffect.m_minStartSpeed, effect.m_particleEffect.m_maxStartSpeed);
                ParticleSystem.ShapeModule shape = root.shape; shape.enabled = false;
                ParticleSystem.EmissionModule emission = root.emission; emission.enabled = true; emission.rateOverTime = 12f;
                clone.SetActive(true);
            }
            fireRoot.transform.position = state.Position + Vector3.up * .25f;
            if (!fireRoot.activeSelf)
            {
                fireRoot.SetActive(true);
                foreach (ParticleSystem system in fireSystems) system.Play(false);
            }
            float speed = Singleton<SimulationManager>.instance.m_simulationTimeSpeed;
            foreach (ParticleSystem system in fireSystems)
            {
                ParticleSystem.MainModule main = system.main; main.simulationSpeed = speed;
                if (speed < .00001f) system.Pause(false);
                else if (system.isPaused) system.Play(false);
            }
        }
        internal void Dispose()
        {
            if (fireRoot != null) { fireRoot.SetActive(false); UnityEngine.Object.Destroy(fireRoot); }
        }
    }
}
