using System;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace CivicPressure
{
    internal static class UnrestRoads
    {
        internal static bool Walkable(ushort id)
        {
            NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
            if (id == 0 || id >= roads.Length) return false;
            NetSegment road = roads[id];
            NetInfo info = road.Info;
            if ((road.m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted | NetSegment.Flags.Collapsed | NetSegment.Flags.Untouchable | NetSegment.Flags.Flooded | NetSegment.Flags.PathFailed)) != NetSegment.Flags.Created ||
                info == null || info.m_netAI == null || info.m_netAI.GetType() != typeof(RoadAI) || info.m_lanes == null) return false;
            foreach (NetInfo.Lane lane in info.m_lanes)
                if ((lane.m_laneType & NetInfo.LaneType.Pedestrian) != 0) return true;
            return false;
        }

        internal static Bezier3 Curve(NetSegment road)
        {
            NetNode[] nodes = Singleton<NetManager>.instance.m_nodes.m_buffer;
            Bezier3 curve = new Bezier3();
            curve.a = nodes[road.m_startNode].m_position; curve.d = nodes[road.m_endNode].m_position;
            NetSegment.CalculateMiddlePoints(curve.a, road.m_startDirection, curve.d, road.m_endDirection,
                (nodes[road.m_startNode].m_flags & NetNode.Flags.Middle) != 0,
                (nodes[road.m_endNode].m_flags & NetNode.Flags.Middle) != 0, out curve.b, out curve.c);
            return curve;
        }

        // Entry node of the leg the crowd is currently on, walked from the recorded prefix.
        // Zero when the prefix no longer resolves. Anchor() normally keeps the prefix empty.
        internal static ushort LegNode(UnrestEvent e)
        {
            if (e == null || e.Leg < 0 || e.Leg >= e.Roads.Length || e.Roads.Length != e.RoadIndices.Length) return 0;
            NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
            ushort node = e.StartNode;
            for (int i = 0; i < e.Leg; i++)
            {
                ushort id = e.Roads[i];
                if (id == 0 || id >= roads.Length) return 0;
                NetSegment road = roads[id];
                if ((road.m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) != NetSegment.Flags.Created ||
                    road.m_buildIndex != e.RoadIndices[i] || (road.m_startNode != node && road.m_endNode != node)) return 0;
                node = road.m_startNode == node ? road.m_endNode : road.m_startNode;
            }
            return node;
        }

        // Forget the legs the crowd already walked, so rebuilding a road the riot destroyed behind it
        // cannot invalidate the route. Leaves the event untouched if the prefix no longer resolves.
        internal static bool Anchor(UnrestEvent e)
        {
            if (e == null || e.Leg <= 0) return e != null && e.Leg == 0;
            ushort node = LegNode(e);
            return node != 0 && MarchRoute.Trim(e, node);
        }

        internal static bool Position(UnrestEvent e, out Vector3 position, out ushort roadId)
        {
            position = Vector3.zero; roadId = 0;
            ushort node = LegNode(e);
            if (node == 0) return false;
            NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
            ushort id = e.Roads[e.Leg];
            if (id == 0 || id >= roads.Length) return false;
            NetSegment road = roads[id];
            // Only the segment the crowd stands on is validated. A road damaged by this event remains a
            // valid gathering location until rebuilt; roads already passed are no longer checked at all.
            if ((road.m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) != NetSegment.Flags.Created ||
                road.m_buildIndex != e.RoadIndices[e.Leg] || (road.m_startNode != node && road.m_endNode != node)) return false;
            position = Curve(road).Position(road.m_startNode == node ? e.Progress : 1 - e.Progress);
            roadId = id; return true;
        }

        // Closest point on a sampled curved road; both sides are relative to its native direction.
        internal static bool Beside(Vector3 building, Vector3[] points, int side)
        {
            float best = float.MaxValue, cross = 0;
            for (int i = 1; i < points.Length; i++)
            {
                Vector3 a = points[i - 1], d = points[i] - a, offset = building - a;
                float length = d.x * d.x + d.z * d.z;
                float t = length == 0 ? 0 : Mathf.Clamp01((offset.x * d.x + offset.z * d.z) / length);
                Vector3 delta = building - (a + t * d);
                float distance = delta.x * delta.x + delta.z * delta.z;
                if (distance < best) { best = distance; cross = d.x * offset.z - d.z * offset.x; }
            }
            return best <= 64 * 64 && (side < 0 ? cross < 0 : cross >= 0);
        }
    }

    // Reverse breadth-first search: at most 512 nodes per simulation tick, with a bounded walk length.
    internal sealed class MarchSearch
    {
        private readonly ushort[] parents, parentRoads;
        private readonly int[] depth;
        private readonly Queue<ushort> queue = new Queue<ushort>();
        private readonly int origin;
        private readonly ushort hall, hallRoad;
        private readonly uint hallIndex;
        private readonly Vector3 destination;
        private readonly System.Random random;
        private ushort choice;
        private ushort wideChoice;
        private int candidates, wideCandidates;
        private readonly ushort fixedStart;
        internal bool Done;
        internal UnrestEvent Result;

        internal MarchSearch(int region, ushort building, System.Random rng)
            : this(region, building, rng, 0) { }

        internal MarchSearch(int region, ushort building, System.Random rng, ushort startNode)
        {
            fixedStart = startNode;
            origin = region; hall = building; random = rng;
            NetNode[] nodes = Singleton<NetManager>.instance.m_nodes.m_buffer;
            parents = new ushort[nodes.Length]; parentRoads = new ushort[nodes.Length]; depth = new int[nodes.Length];
            Building b = Singleton<BuildingManager>.instance.m_buildings.m_buffer[hall];
            hallIndex = b.m_buildIndex; destination = b.m_position; hallRoad = b.m_accessSegment;
            if (!CivicSimulation.Live(b) || (b.m_flags & (Building.Flags.Collapsed | Building.Flags.BurnedDown)) != 0) { Done = true; return; }
            if (!UnrestRoads.Walkable(hallRoad)) { Done = true; return; }
            ushort start = Singleton<NetManager>.instance.m_segments.m_buffer[hallRoad].m_startNode;
            parents[start] = start; queue.Enqueue(start);
        }

        internal void Step()
        {
            if (Done) return;
            NetManager net = Singleton<NetManager>.instance;
            DistrictManager districts = Singleton<DistrictManager>.instance;
            for (int budget = 0; budget < 512 && queue.Count > 0; budget++)
            {
                ushort node = queue.Dequeue();
                Vector3 pos = net.m_nodes.m_buffer[node].m_position;
                float distance = Vector3.Distance(pos, destination);
                if (fixedStart != 0 && node == fixedStart) choice = node;
                if (fixedStart == 0 && depth[node] > 0 && distance >= 150 && distance <= 2000 &&
                    (origin < 0 || districts.GetDistrict(pos) == origin))
                {
                    if (random.Next(++candidates) == 0) choice = node;
                    if (UnrestRules.PreferredStartRoad(net.m_segments.m_buffer[parentRoads[node]].Info.m_halfWidth) &&
                        random.Next(++wideCandidates) == 0) wideChoice = node;
                }
                if (depth[node] >= 256) continue;
                for (int slot = 0; slot < 8; slot++)
                {
                    ushort roadId = net.m_nodes.m_buffer[node].GetSegment(slot);
                    if (roadId == hallRoad || !UnrestRoads.Walkable(roadId)) continue;
                    NetSegment road = net.m_segments.m_buffer[roadId];
                    ushort other = road.m_startNode == node ? road.m_endNode : road.m_startNode;
                    if (other == 0 || other >= parents.Length || parents[other] != 0) continue;
                    parents[other] = node; parentRoads[other] = roadId; depth[other] = depth[node] + 1; queue.Enqueue(other);
                }
            }
            if (queue.Count != 0) return;
            Done = true;
            if (fixedStart == 0 && wideChoice != 0) choice = wideChoice;
            if (choice == 0) return;
            List<ushort> route = new List<ushort>();
            ushort cursor = choice;
            while (parents[cursor] != cursor) { route.Add(parentRoads[cursor]); cursor = parents[cursor]; }
            route.Add(hallRoad);
            uint[] indices = new uint[route.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = net.m_segments.m_buffer[route[i]].m_buildIndex;
            Result = new UnrestEvent { Origin = origin, Hall = hall, HallIndex = hallIndex, StartNode = choice,
                Roads = route.ToArray(), RoadIndices = indices };
        }
    }
}
