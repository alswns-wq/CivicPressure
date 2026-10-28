using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    // Immutable sampled route shared with rendering. Only the simulation thread reads road buffers.
    internal sealed class DevRoadRibbon
    {
        internal Vector3[] Points;
        internal float[] Widths, Distances;
        internal float Front;
        internal float Limit = float.PositiveInfinity;
        internal DevRoadRibbon WithFront(Vector3 position)
        {
            float station, side; Project(position, out station, out side);
            return new DevRoadRibbon { Points = Points, Widths = Widths, Distances = Distances, Front = station, Limit = Limit };
        }
        internal void Project(Vector3 position, out float station, out float side)
        {
            station = side = 0; float best = float.MaxValue;
            for (int i = 1; i < Points.Length; i++)
            {
                Vector3 d = Points[i] - Points[i - 1]; d.y = 0;
                float t = d.sqrMagnitude < .0001f ? 0 : Mathf.Clamp01(Vector3.Dot(position - Points[i - 1], d) / d.sqrMagnitude);
                Vector3 at = Vector3.Lerp(Points[i - 1], Points[i], t);
                float distance = (at - position).sqrMagnitude;
                if (distance >= best) continue;
                best = distance; station = Mathf.Lerp(Distances[i - 1], Distances[i], t);
                Vector3 normal = new Vector3(d.z, 0, -d.x).normalized;
                side = Vector3.Dot(position - at, normal);
            }
        }
        internal Vector3 At(float station, float side, out Vector3 forward)
        {
            station = Mathf.Clamp(station, 0, Math.Min(Limit, Distances[Distances.Length - 1]));
            int found = Array.BinarySearch(Distances, station);
            int i = Math.Max(1, Math.Min(Distances.Length - 1, found >= 0 ? found : ~found));
            float length = Distances[i] - Distances[i - 1];
            float t = length < .001f ? 0 : (station - Distances[i - 1]) / length;
            forward = Points[i] - Points[i - 1]; forward.y = 0;
            forward = forward.sqrMagnitude > .0001f ? forward.normalized : Vector3.forward;
            float width = Math.Min(Widths[i - 1], Widths[i]);
            return Vector3.Lerp(Points[i - 1], Points[i], t) + new Vector3(forward.z, 0, -forward.x) * Mathf.Clamp(side, -width, width);
        }
        internal Vector3 Constrain(Vector3 position)
        { float station, side; Vector3 forward; Project(position, out station, out side); return At(station, side, out forward); }
    }
    internal static class DevRoadCorridor
    {
        private static ushort[] route;
        private static ushort[] planned;
        private static uint[] plannedIndices;
        private static UnrestEvent incident;
        private static DevRoadRibbon cached;
        internal static DevRoadRibbon Make(UnrestEvent e, Vector3 position)
        {
            if (incident != e) { incident = e; route = null; cached = null; planned = null; }
            if (cached != null && route != e.Roads && Suffix(e.Roads, e.RoadIndices, planned, plannedIndices)) route = e.Roads;
            if (cached == null || route != e.Roads || planned == null)
            {
                route = e.Roads;
                List<Vector3> points = new List<Vector3>(); List<float> widths = new List<float>();
                NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
                ushort node = UnrestRoads.LegNode(e);
                // Preserve the walked prefix on a connected reroute, so stragglers keep their own road.
                if (cached != null && node != 0)
                {
                    Vector3 entry = Singleton<NetManager>.instance.m_nodes.m_buffer[node].m_position;
                    float join, side; Vector3 forward; cached.Project(entry, out join, out side);
                    if ((cached.At(join, 0, out forward) - entry).sqrMagnitude < 1)
                        for (int i = 0; i < cached.Points.Length && cached.Distances[i] < join - .01f; i++)
                        { points.Add(cached.Points[i]); widths.Add(cached.Widths[i]); }
                    if (points.Count > 0) { points.Add(entry); widths.Add(Math.Max(.5f, roads[e.Roads[e.Leg]].Info.m_halfWidth - 1)); }
                }
                int built = 0;
                for (int leg = e.Leg; leg < e.Roads.Length; leg++)
                {
                    ushort id = e.Roads[leg]; if (id == 0 || id >= roads.Length || roads[id].m_buildIndex != e.RoadIndices[leg]) break;
                    NetSegment road = roads[id]; if (road.m_startNode != node && road.m_endNode != node) break;
                    Add(points, widths, road, road.m_startNode == node, leg == e.Roads.Length - 1);
                    built++;
                    node = road.m_startNode == node ? road.m_endNode : road.m_startNode;
                }
                if (points.Count < 2) return null;
                float[] distances = new float[points.Count];
                for (int i = 1; i < points.Count; i++) distances[i] = distances[i - 1] + Vector3.Distance(points[i], points[i - 1]);
                cached = new DevRoadRibbon { Points = points.ToArray(), Widths = widths.ToArray(), Distances = distances };
                planned = built == e.Roads.Length - e.Leg ? (ushort[])e.Roads.Clone() : null;
                plannedIndices = planned == null ? null : (uint[])e.RoadIndices.Clone();
            }
            DevRoadRibbon snapshot = cached.WithFront(position);
            NetSegment[] liveRoads = Singleton<NetManager>.instance.m_segments.m_buffer;
            ushort entryNode = UnrestRoads.LegNode(e);
            for (int leg = e.Leg; leg < e.Roads.Length && entryNode != 0; leg++)
            {
                ushort id = e.Roads[leg];
                if (id == 0 || id >= liveRoads.Length || liveRoads[id].m_buildIndex != e.RoadIndices[leg] || !UnrestRoads.Walkable(id))
                {
                    float stop, side; snapshot.Project(Singleton<NetManager>.instance.m_nodes.m_buffer[entryNode].m_position, out stop, out side);
                    snapshot.Limit = stop; break;
                }
                NetSegment road = liveRoads[id]; entryNode = road.m_startNode == entryNode ? road.m_endNode : road.m_startNode;
            }
            return snapshot;
        }
        internal static bool Suffix(ushort[] roads, uint[] indices, ushort[] full, uint[] fullIndices)
        {
            if (full == null || fullIndices == null || roads.Length > full.Length || roads.Length != indices.Length || full.Length != fullIndices.Length) return false;
            int offset = full.Length - roads.Length;
            for (int i = 0; i < roads.Length; i++) if (roads[i] != full[offset + i] || indices[i] != fullIndices[offset + i]) return false;
            return true;
        }
        private static void Add(List<Vector3> points, List<float> widths, NetSegment road, bool forward, bool final)
        {
            ColossalFramework.Math.Bezier3 curve = UnrestRoads.Curve(road);
            int steps = Math.Max(8, Math.Min(128, (int)(road.m_averageLength / 3) + 1));
            float width = Math.Max(.5f, road.Info.m_halfWidth - 1);
            for (int i = points.Count == 0 ? 0 : 1; i <= steps; i++)
            { float t = i / (float)steps * (final ? .5f : 1); points.Add(curve.Position(forward ? t : 1 - t)); widths.Add(width); }
        }
    }
}
