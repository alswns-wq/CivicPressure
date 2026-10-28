using System;
using System.IO;

namespace CivicPressure
{
    // No game types: these rules and the save format can be checked without running Unity.
    internal static class CrowdVisualRules
    {
        internal static float Interval(float distanceSquared) { return distanceSquared<=100*100 ? 1f/30f : distanceSquared<=250*250 ? 1f/15f : .2f; }
        internal static bool InViewport(float x,float y,float z,float margin)
        { return z>0 && x>=-margin && x<=1+margin && y>=-margin && y<=1+margin; }
    }
    internal static class UnrestRules
    {
        internal static bool SameTarget(ushort expected,uint saved,ushort current,uint actual,bool live)
        { return expected!=0 && expected==current && saved==actual && live; }
        internal const int MaxBuildingDamage = 48;
        internal static bool SuppressionFails(double roll) { return roll < .15; }
        internal static bool PoliceReady(bool police, long arrived, long now)
        { return police && arrived > 0 && now >= arrived && now - arrived >= GameClock.MinuteTicks * 30; }
        internal static int DamageKind(double roll) {
            return roll < .20 ? 2 : roll < .90 ? 1 : 0;
        }
        internal static bool DamageRoad(double roll) {
            return roll < .15;
        }
        internal static int CrowdColumns(bool riot) { return riot ? 8 : 7; }
        internal static int CrowdCount(bool riot) { return CrowdColumns(riot) * (riot ? 11 : 7); }
        internal static float Speed(bool riot) {
            return riot ? 150 : 170;
        }
        internal static bool PreferredStartRoad(float halfWidth) { return halfWidth >= 12f; }
        internal static bool CanProtest(double score) { return !double.IsNaN(score) && score < 40; }
        internal static bool CanRiot(double score) { return !double.IsNaN(score) && score < 20; }
        internal static bool Resolved(int residents, int[] affected)
        {
            if (residents <= 0 || affected == null) return false;
            foreach (int count in affected) if (count != 0) return false;
            return true;
        }
        internal static bool Disbands(bool riot, bool elapsed, bool resolved, bool rebuilt, bool police)
        {
            return (elapsed || resolved) && (!riot || (rebuilt && police));
        }
        internal static double Chance(double hourly, double multiplier, double hours)
        {
            return 1 - Math.Exp(-Math.Max(0, hourly) * Math.Max(0, multiplier) * Math.Max(0, hours));
        }
    }

    internal sealed class DamageRecord
    {
        internal ushort Building;
        internal uint BuildIndex;
        internal float X, Y, Z;
        internal int Service;
        internal bool Destroyed;
    }

    internal sealed class PoliceUnit { internal ushort Vehicle, Station; internal uint StationIndex; }

    internal sealed class UnrestEvent
    {
        internal int Origin; // -1: city without ordinary districts; 0: residents outside districts.
        internal bool Riot, Arrived, Police;
        internal bool Manual;
        internal int Meeting; // 0 unchecked, 1 no offer, 2 pending, 3 accepted, 4 declined, 5 expired
        internal long Started, LastDamage;
        internal long PoliceAt;
        internal ushort DeployedVehicle, DeployedStation;
        internal PoliceUnit[] Support = new PoliceUnit[0];
        internal uint StationIndex;
        internal ushort Hall;
        internal uint HallIndex;
        internal ushort StartNode;
        internal ushort[] Roads = new ushort[0];
        internal uint[] RoadIndices = new uint[0];
        internal int Leg;
        internal float Progress;
        internal int RoadDamage;
        internal int TotalDamage;
        internal float X, Y, Z;
        internal DamageRecord[] Damage = new DamageRecord[0];
    }

    // Route surgery without game types: the crowd forgets the legs it already walked, so a road the riot
    // wrecked behind it can be rebuilt without invalidating the march.
    internal static class MarchRoute
    {
        internal static bool Trim(UnrestEvent e, ushort node)
        {
            if (e == null || node == 0 || e.Roads == null || e.RoadIndices == null ||
                e.Leg <= 0 || e.Leg >= e.Roads.Length || e.Roads.Length != e.RoadIndices.Length) return false;
            int keep = e.Roads.Length - e.Leg;
            ushort[] roads = new ushort[keep]; uint[] indices = new uint[keep];
            Array.Copy(e.Roads, e.Leg, roads, 0, keep);
            Array.Copy(e.RoadIndices, e.Leg, indices, 0, keep);
            e.Roads = roads; e.RoadIndices = indices; e.StartNode = node; e.Leg = 0;
            return true;
        }
    }

    internal static class DelegationMeeting
    {
        internal static bool Roll(UnrestEvent e,bool local,double roll)
        {
            if(e.Meeting!=0 || e.Riot || !e.Arrived || !local)return false;
            e.Meeting=roll>=0 && roll<.03 ? 2 : 1;return e.Meeting==2;
        }
        internal static bool Decide(UnrestEvent e,bool local,long now,long deadline,bool accept)
        {
            if(e.Meeting!=2)return false;
            if(e.Riot || !e.Arrived || !local || now>=deadline){e.Meeting=5;return false;}
            e.Meeting=accept?3:4;return true;
        }
        internal static double Bonus(UnrestEvent e){return e.Meeting==3 && !e.Riot ? 3 : 0;}
    }
    internal sealed class UnrestState
    {
        internal long NextCheck, NextEligible;
        internal UnrestEvent Event;
        internal DamageRecord[] ProtectedFires = new DamageRecord[0];
    }

    internal static class UnrestSave
    {
        internal const string Key = "CivicPressure.Unrest";
        internal const int MaxRoads = 4096;
        internal static byte[] Encode(UnrestState state)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(0x43505533); writer.Write(state.Event == null ? 8 : state.Event.Support.Length > 4 ? 10 : state.Event.Support.Length > 2 ? 9 : 8);
                writer.Write(state.NextCheck); writer.Write(state.NextEligible);
                UnrestEvent e = state.Event;
                writer.Write(e != null);
                if (e != null)
                {
                    writer.Write(e.Origin); writer.Write(e.Riot); writer.Write(e.Arrived); writer.Write(e.Police);
                    writer.Write(e.Started); writer.Write(e.LastDamage); writer.Write(e.Hall); writer.Write(e.HallIndex);
                    writer.Write(e.StartNode); writer.Write(e.Leg); writer.Write(e.Progress); writer.Write(e.RoadDamage);
                    writer.Write(e.TotalDamage); writer.Write(e.X); writer.Write(e.Y); writer.Write(e.Z);
                    writer.Write(e.Roads.Length);
                    for (int i = 0; i < e.Roads.Length; i++) { writer.Write(e.Roads[i]); writer.Write(e.RoadIndices[i]); }
                    writer.Write(e.Damage.Length);
                    foreach (DamageRecord d in e.Damage)
                    {
                        writer.Write(d.Building); writer.Write(d.BuildIndex); writer.Write(d.X); writer.Write(d.Y); writer.Write(d.Z);
                        writer.Write(d.Service); writer.Write(d.Destroyed);
                    }
                    writer.Write(e.Manual);
                    writer.Write(e.PoliceAt);
                    writer.Write(e.DeployedVehicle); writer.Write(e.DeployedStation); writer.Write(e.StationIndex);
                }
                writer.Write(state.ProtectedFires.Length);
                foreach (DamageRecord fire in state.ProtectedFires) { writer.Write(fire.Building); writer.Write(fire.BuildIndex); }
                if (e != null) {
                    writer.Write(e.Support.Length);
                    foreach (PoliceUnit unit in e.Support) { writer.Write(unit.Vehicle); writer.Write(unit.Station); writer.Write(unit.StationIndex); }
                }
                if(e!=null) writer.Write(e.Meeting);
                return stream.ToArray();
            }
        }
        private static long Time(BinaryReader r)
        {
            long ticks = r.ReadInt64();
            if (ticks < 0 || ticks > DateTime.MaxValue.Ticks) throw new InvalidDataException("Invalid game time");
            return ticks;
        }
        private static float Number(BinaryReader r)
        {
            float value = r.ReadSingle();
            if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 100000) throw new InvalidDataException("Invalid coordinate");
            return value;
        }
        internal static UnrestState Decode(byte[] data)
        {
            if (data == null || data.Length == 0) return new UnrestState();
            if (data.Length > 600000) throw new InvalidDataException("Unrest save too large");
            using (BinaryReader r = new BinaryReader(new MemoryStream(data, false)))
            {
                if (r.ReadInt32() != 0x43505533) throw new InvalidDataException("Unsupported unrest save");
                int version = r.ReadInt32();
                if (version < 1 || version > 10) throw new InvalidDataException("Unsupported unrest save");
                UnrestState state = new UnrestState { NextCheck = Time(r), NextEligible = Time(r) };
                if (r.ReadBoolean())
                {
                    UnrestEvent e = new UnrestEvent();
                    e.Origin = r.ReadInt32(); e.Riot = r.ReadBoolean(); e.Arrived = r.ReadBoolean(); e.Police = r.ReadBoolean();
                    e.Started = Time(r); e.LastDamage = Time(r); e.Hall = r.ReadUInt16(); e.HallIndex = r.ReadUInt32();
                    e.StartNode = r.ReadUInt16(); e.Leg = r.ReadInt32(); e.Progress = Number(r); e.RoadDamage = r.ReadInt32();
                    e.TotalDamage = r.ReadInt32(); e.X = Number(r); e.Y = Number(r); e.Z = Number(r);
                    int count = r.ReadInt32();
                    if (e.Origin < -1 || e.Origin > 255 || e.Hall == 0 || e.StartNode == 0 || count < 1 || count > MaxRoads ||
                        e.Leg < 0 || e.Leg >= count || e.Progress < 0 || e.Progress > 1 || e.RoadDamage < 0 || e.RoadDamage > (version < 6 ? 1 : 8) ||
                        e.TotalDamage < 0 || e.TotalDamage > (version < 3 ? 2 : version < 6 ? 12 : UnrestRules.MaxBuildingDamage) || (!e.Riot && e.TotalDamage != 0))
                        throw new InvalidDataException("Invalid event");
                    e.Roads = new ushort[count]; e.RoadIndices = new uint[count];
                    for (int i = 0; i < count; i++)
                    {
                        e.Roads[i] = r.ReadUInt16(); e.RoadIndices[i] = r.ReadUInt32();
                        if (e.Roads[i] == 0) throw new InvalidDataException("Invalid road");
                    }
                    count = r.ReadInt32();
                    if (count < 0 || count > e.TotalDamage || (!e.Riot && (count != 0 || e.RoadDamage != 0 || e.Police)))
                        throw new InvalidDataException("Invalid damage ledger");
                    e.Damage = new DamageRecord[count];
                    for (int i = 0; i < count; i++)
                    {
                        DamageRecord d = new DamageRecord { Building = r.ReadUInt16(), BuildIndex = r.ReadUInt32(),
                            X = Number(r), Y = Number(r), Z = Number(r), Service = r.ReadInt32(), Destroyed = r.ReadBoolean() };
                        if (d.Building == 0) throw new InvalidDataException("Invalid victim");
                        for (int j = 0; j < i; j++) if (e.Damage[j].Building == d.Building) throw new InvalidDataException("Duplicate victim");
                        e.Damage[i] = d;
                    }
                    if (version >= 2) e.Manual = r.ReadBoolean();
                    if (version >= 4) e.PoliceAt = Time(r);
                    if (version >= 5)
                    {
                        e.DeployedVehicle = r.ReadUInt16(); e.DeployedStation = r.ReadUInt16(); e.StationIndex = r.ReadUInt32();
                        if (e.DeployedVehicle != 0 && (!e.Riot || e.DeployedStation == 0)) throw new InvalidDataException("Invalid deployment");
                    }
                    if (!e.Police && e.PoliceAt != 0) throw new InvalidDataException("Invalid police timer");
                    state.Event = e;
                }
                if (version >= 4)
                {
                    int count = r.ReadInt32();
                    if (count < 0 || count > 4096) throw new InvalidDataException("Invalid protected fires");
                    state.ProtectedFires = new DamageRecord[count];
                    for (int i = 0; i < count; i++)
                    {
                        DamageRecord fire = new DamageRecord { Building = r.ReadUInt16(), BuildIndex = r.ReadUInt32() };
                        if (fire.Building == 0) throw new InvalidDataException("Invalid protected fire");
                        state.ProtectedFires[i] = fire;
                    }
                }
                if (version >= 7 && state.Event != null) {
                    UnrestEvent e = state.Event;
                    int count = r.ReadInt32();
                    if (count < 0 || count > (version >= 10 ? 65534 : version >= 9 ? 4 : 2) || (count > 0 && (!e.Riot || e.DeployedVehicle == 0))) throw new InvalidDataException("Invalid support count");
                    e.Support = new PoliceUnit[count];
                    for (int i = 0; i < count; i++) {
                        PoliceUnit unit = new PoliceUnit { Vehicle = r.ReadUInt16(), Station = r.ReadUInt16(), StationIndex = r.ReadUInt32() };
                        if (unit.Vehicle == 0 || unit.Station == 0 || unit.Vehicle == e.DeployedVehicle) throw new InvalidDataException("Invalid support vehicle");
                        for (int j = 0; j < i; j++) if (e.Support[j].Vehicle == unit.Vehicle) throw new InvalidDataException("Duplicate support vehicle");
                        e.Support[i] = unit;
                    }
                }
                if(state.Event!=null) {
                    state.Event.Meeting=version>=8 ? r.ReadInt32() : state.Event.Arrived ? 1 : 0;
                    if(state.Event.Meeting<0 || state.Event.Meeting>5)throw new InvalidDataException("Invalid delegation state");
                }
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing save data");
                return state;
            }
        }
    }
}
