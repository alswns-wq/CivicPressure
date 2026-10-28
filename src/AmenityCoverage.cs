using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    // Per-census spatial index. No path requests and no per-citizen facility scans.
    internal sealed class AmenityCoverage
    {
        private readonly Dictionary<long, List<Vector3>> transit = new Dictionary<long, List<Vector3>>();
        private readonly Dictionary<long, List<Vector3>> parks = new Dictionary<long, List<Vector3>>();
        private int nodeCursor = 1, parkCursor = 1;
        private static long Key(int x, int z) { return ((long)x << 32) | (uint)z; }
        private static int Cell(float value) { return (int)Math.Floor(value / 800); }
        private static void Add(Dictionary<long, List<Vector3>> grid, Vector3 point)
        {
            long key = Key(Cell(point.x), Cell(point.z)); List<Vector3> list;
            if (!grid.TryGetValue(key, out list)) { list = new List<Vector3>(); grid.Add(key, list); }
            list.Add(point);
        }
        internal static bool Within(float dx, float dz, float radius)
        { return dx * dx + dz * dz <= radius * radius; }
        private static bool Covered(Dictionary<long, List<Vector3>> grid, Vector3 point, float radius)
        {
            for (int x = Cell(point.x - radius); x <= Cell(point.x + radius); x++)
                for (int z = Cell(point.z - radius); z <= Cell(point.z + radius); z++) {
                    List<Vector3> list; if (!grid.TryGetValue(Key(x, z), out list)) continue;
                    foreach (Vector3 stop in list) if (Within(stop.x - point.x, stop.z - point.z, radius)) return true;
                }
            return false;
        }
        internal static bool Running(TransportLine.Flags flags)
        {
            TransportLine.Flags required = TransportLine.Flags.Created | TransportLine.Flags.Complete;
            // Day-only/night-only routes still provide access; do not oscillate at dawn/dusk.
            return (flags & required) == required &&
                (flags & (TransportLine.Flags.Deleted | TransportLine.Flags.Temporary | TransportLine.Flags.Invalid)) == 0 &&
                (flags & (TransportLine.Flags.DisabledDay | TransportLine.Flags.DisabledNight)) !=
                (TransportLine.Flags.DisabledDay | TransportLine.Flags.DisabledNight);
        }
        internal bool Step()
        {
            NetNode[] nodes = Singleton<NetManager>.instance.m_nodes.m_buffer;
            TransportLine[] lines = Singleton<TransportManager>.instance.m_lines.m_buffer;
            if (nodeCursor < nodes.Length) {
                int end = Math.Min(nodeCursor + 4096, nodes.Length);
                for (; nodeCursor < end; nodeCursor++) {
                    NetNode node = nodes[nodeCursor]; ushort line = node.m_transportLine;
                    if ((node.m_flags & (NetNode.Flags.Created | NetNode.Flags.Deleted)) == NetNode.Flags.Created &&
                        line != 0 && line < lines.Length && Running(lines[line].m_flags)) Add(transit, node.m_position);
                }
                return false;
            }
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            if (parkCursor < buildings.Length) {
                int end = Math.Min(parkCursor + 4096, buildings.Length);
                for (; parkCursor < end; parkCursor++) {
                    Building b = buildings[parkCursor];
                    if (!CivicSimulation.Live(b) || b.Info == null ||
                        (b.m_flags & (Building.Flags.Active | Building.Flags.Completed)) != (Building.Flags.Active | Building.Flags.Completed) ||
                        (b.m_flags & (Building.Flags.Abandoned | Building.Flags.Collapsed | Building.Flags.Demolishing)) != 0) continue;
                    if (b.Info.m_buildingAI is ParkAI || b.Info.m_buildingAI is ParkBuildingAI) Add(parks, b.m_position);
                }
                return false;
            }
            return true;
        }
        internal int Missing(Vector3 home)
        {
            int mask = Covered(transit, home, 800) ? 0 : 1 << 15;
            if (!Covered(parks, home, 600)) mask |= 1 << 16;
            return mask;
        }
    }
}
