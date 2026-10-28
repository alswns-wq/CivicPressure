using System.Threading;
using ColossalFramework;

namespace CivicPressure
{
    internal static class DevRiotPassage
    {
        // The same path decision follows every linked path unit, so entering cars are not stopped again.
        private static readonly int[] roots = new int[262144];
        internal static void Reset() { System.Array.Clear(roots, 0, roots.Length); }
        internal static bool Allows(Closure c, ushort road, uint root, VehicleInfo.VehicleCategory category)
        {
            if (c == null || !c.DevTraffic || road != c.Road || root == 0 || category != VehicleInfo.VehicleCategory.PassengerCar) return false;
            uint hash = unchecked(root * 2654435761u ^ c.BuildIndex * 2246822519u ^ c.Road);
            hash ^= hash >> 16;
            return hash % 100 < 20;
        }
        internal static bool Car(Closure c, Vehicle car)
        {
            VehicleInfo info = car.Info;
            if (info == null || info.m_vehicleAI.GetType() != typeof(PassengerCarAI) || car.m_path == 0 || car.m_path >= roots.Length) return false;
            uint root = (uint)Thread.VolatileRead(ref roots[car.m_path]);
            return Allows(c, c == null ? (ushort)0 : c.Road, root == 0 ? car.m_path : root, VehicleInfo.VehicleCategory.PassengerCar);
        }
        internal static void Completed(uint unit)
        {
            if (unit == 0 || unit >= roots.Length) return;
            PathUnit[] paths = Singleton<PathManager>.instance.m_pathUnits.m_buffer;
            if (unit >= paths.Length || (paths[unit].m_pathFindFlags & 4) == 0) return;
            uint cursor = unit;
            for (int count = 0; cursor != 0 && cursor < paths.Length && cursor < roots.Length && count < 4096; count++)
            { Thread.VolatileWrite(ref roots[cursor], (int)unit); cursor = paths[cursor].m_nextPathUnit; }
        }
    }
}
