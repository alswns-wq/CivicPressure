using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    internal static partial class CivicUnrest
    {
        private static UnrestEvent recoveryEvent, endRequested;
        private static MarchSearch recoverySearch;
        private static long nextRecovery;
        private static ushort recoveryRoad;
        private static uint recoveryIndex;
        private static float recoveryProgress;
        private static bool recoveryReverse;
        private static readonly Dictionary<ushort,uint> triedRoads = new Dictionary<ushort,uint>();

        internal static UnrestEvent IncidentForRecovery { get { lock(Gate) { return preserved==null ? state.Event : null; } } }
        internal static void RequestEnd(UnrestEvent expected)
        {
            lock (Gate) { if (preserved == null && object.ReferenceEquals(expected,state.Event)) endRequested = expected; }
        }

        private static void ClearRecovery()
        {
            recoveryEvent = null; recoverySearch = null; nextRecovery = 0;
            triedRoads.Clear();
        }

        // Validate the whole remaining route, including connections, before committing a replacement.
        internal static bool RecoveryRouteValid(UnrestEvent e, NetSegment[] roads)
        {
            if (e == null || e.Roads.Length == 0 || e.Roads.Length != e.RoadIndices.Length) return false;
            ushort node = e.StartNode;
            for (int i = 0; i < e.Roads.Length; i++) {
                ushort id = e.Roads[i];
                if (id == 0 || id >= roads.Length) return false;
                NetSegment road = roads[id];
                if ((road.m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted | NetSegment.Flags.Collapsed)) != NetSegment.Flags.Created ||
                    road.m_buildIndex != e.RoadIndices[i] || node == 0 || (road.m_startNode != node && road.m_endNode != node)) return false;
                node = road.m_startNode == node ? road.m_endNode : road.m_startNode;
            }
            return true;
        }

        private static void RecoverRoute(UnrestEvent e, DateTime now)
        {
            if (recoveryEvent != e) { ClearRecovery(); recoveryEvent = e; }
            if (now.Ticks < nextRecovery) return;
            NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
            if (recoverySearch == null) {
                if (RecoveryRouteValid(e, roads)) { triedRoads.Clear(); nextRecovery = now.Ticks + GameClock.Minutes(1); return; }
                if (!TargetExists(e)) { nextRecovery = now.Ticks + GameClock.Minutes(1); return; }
                // Scan only on failure; failed attempts are throttled. Reattach only on the same elevation nearby.
                float best = 24 * 24; recoveryRoad = 0;
                Vector3 center = new Vector3(e.X, e.Y, e.Z);
                foreach (ushort id in RecoveryRoads(center)) {
                    if (!UnrestRoads.Walkable((ushort)id)) continue;
                    NetSegment road = roads[id];
                    uint triedIndex;
                    if (triedRoads.TryGetValue(id, out triedIndex) && triedIndex == road.m_buildIndex) continue;
                    Vector3 a = Singleton<NetManager>.instance.m_nodes.m_buffer[road.m_startNode].m_position;
                    Vector3 b = Singleton<NetManager>.instance.m_nodes.m_buffer[road.m_endNode].m_position;
                    if (center.x < Math.Min(a.x,b.x)-128 || center.x > Math.Max(a.x,b.x)+128 ||
                        center.z < Math.Min(a.z,b.z)-128 || center.z > Math.Max(a.z,b.z)+128) continue;
                    ColossalFramework.Math.Bezier3 curve = UnrestRoads.Curve(road);
                    for (int sample = 0; sample <= 32; sample++) {
                        float t = sample / 32f; Vector3 p = curve.Position(t); Vector3 d = p-center;
                        float distance = d.sqrMagnitude;
                        if (Math.Abs(d.y) > 4 || distance >= best) continue;
                        best = distance; recoveryRoad = (ushort)id; recoveryIndex = road.m_buildIndex; recoveryProgress = t;
                    }
                }
                if (recoveryRoad == 0) {
                    triedRoads.Clear();
                    nextRecovery = now.Ticks + GameClock.Minutes(30);
                    return;
                }
                recoveryReverse = false;
                recoverySearch = new MarchSearch(e.Origin,e.Hall,Random,roads[recoveryRoad].m_endNode,recoveryRoad);
            }
            recoverySearch.Step();
            if (!recoverySearch.Done) return;
            UnrestEvent result = recoverySearch.Result;
            NetSegment selected = roads[recoveryRoad];
            if (result == null && !recoveryReverse && selected.m_buildIndex == recoveryIndex && UnrestRoads.Walkable(recoveryRoad)) {
                recoveryReverse = true;
                recoverySearch = new MarchSearch(e.Origin,e.Hall,Random,selected.m_startNode,recoveryRoad);
                return;
            }
            recoverySearch = null; nextRecovery = now.Ticks + GameClock.Minutes(1);
            triedRoads[recoveryRoad] = recoveryIndex;
            if (result == null || !TargetExists(e) || result.Hall != e.Hall || result.HallIndex != e.HallIndex || selected.m_buildIndex != recoveryIndex || !UnrestRoads.Walkable(recoveryRoad)) return;
            if (result.Roads[result.Roads.Length-1] != Singleton<BuildingManager>.instance.m_buildings.m_buffer[e.Hall].m_accessSegment) return;
            // If already on the hall road, the search's final leg is that same road: keep one leg.
            List<ushort> route = new List<ushort>(); List<uint> indices = new List<uint>();
            route.Add(recoveryRoad); indices.Add(recoveryIndex);
            if (result.Roads.Length != 1 || result.Roads[0] != recoveryRoad) {
                foreach (ushort id in result.Roads) { if (id == recoveryRoad) return; route.Add(id); }
                indices.AddRange(result.RoadIndices);
            }
            ushort entry = recoveryReverse ? selected.m_endNode : selected.m_startNode;
            UnrestEvent candidate = new UnrestEvent { Roads=route.ToArray(), RoadIndices=indices.ToArray(), StartNode=entry };
            if (!RecoveryRouteValid(candidate,roads)) return;
            triedRoads.Clear();
            e.Roads=candidate.Roads; e.RoadIndices=candidate.RoadIndices; e.StartNode=entry;
            e.Leg=0; e.Progress=recoveryReverse ? 1-recoveryProgress : recoveryProgress; e.Arrived=false;
            position=UnrestRoads.Curve(selected).Position(recoveryProgress);
            e.X=position.x; e.Y=position.y; e.Z=position.z;
            onward=null; nextOnward=0; passedRoad=0; passedIndex=0;
            DevRoadCorridor.Invalidate();
            PoliceDeployment.RouteChanged(e);
            Debug.Log("[CivicPressure] March route recovered: road="+recoveryRoad+", legs="+e.Roads.Length);
        }

        private static List<ushort> RecoveryRoads(Vector3 center)
        {
            NetManager net=Singleton<NetManager>.instance;
            List<ushort> found=new List<ushort>();
            int x=Mathf.Clamp((int)(center.x/64+135),0,269), z=Mathf.Clamp((int)(center.z/64+135),0,269);
            for(int row=Math.Max(0,z-2);row<=Math.Min(269,z+2);row++)
                for(int col=Math.Max(0,x-2);col<=Math.Min(269,x+2);col++) {
                    ushort id=net.m_segmentGrid[row*270+col];
                    for(int guard=0;id!=0 && id<net.m_segments.m_buffer.Length && guard<512;guard++) {
                        found.Add(id); id=net.m_segments.m_buffer[id].m_nextGridSegment;
                    }
                }
            return found;
        }

        private static void EndCurrentIncident(CityView city, DateTime now)
        {
            UnrestEvent e=state.Event;
            if (e==null) return;
            PreserveFires(e);
            PoliceDeployment.Release();
            CivicRecords.Finish(e,city,now,L.T("시위·폭동 강제종료"));
            state.Event=null; state.NextEligible=now.Ticks+GameClock.Hours(24);
            closure=null; policeTarget=0; policeVehicle=0; policeStation=0; dispatch=false; nextDispatch=0;
            passedRoad=0; passedIndex=0; onward=null; nextOnward=0; meetingTarget=null;
            search=null; manualRequest=0; manualSearch=false; riotSearch=false;
            ClearRecovery(); CivicComplaints.End(e.Origin,now.Ticks);
            message=L.T("시위·폭동을 강제종료했습니다. 남은 민원은 유지됩니다.");
            view=new UnrestView {Text=message};
            Debug.Log("[CivicPressure] Incident manually ended; complaints retained.");
        }
    }
}
