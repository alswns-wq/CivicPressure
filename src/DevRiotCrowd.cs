using System;
using System.Collections.Generic;
using UnityEngine;

namespace CivicPressure
{
    internal sealed class DevAttackBudget
    {
        private readonly int[] counts=new int[88];
        private readonly int[] limits=new int[88];
        private readonly System.Random random=new System.Random();
        internal int Limit(int index) { if(index<0 || index>=limits.Length) return 0; if(limits[index]==0) limits[index]=random.Next(5,8); return limits[index]; }
        internal bool Available(int index) { return index>=0 && index<counts.Length && counts[index]<Limit(index); }
        internal void Count(int index) { if(Available(index)) counts[index]++; }
        internal void Reset() { Array.Clear(counts,0,counts.Length); Array.Clear(limits,0,limits.Length); }
    }
    internal sealed class DevCrowdPresence
    {
        internal static volatile DevCrowdPresence Current;
        internal long Stamp;
        internal Vector3[] Positions;
        internal Vector3[] DamagePositions;
        internal uint Frame;
        internal int[] Cells;
        internal int[] LargeCells;
        internal Vector3 Center;
        internal bool HasCenter;
        internal float Near(Vector3 target, out Vector3 origin)
        {
            origin = target; float nearest = float.MaxValue; int helpers = 0;
            foreach (Vector3 person in Positions)
            {
                Vector3 delta = target - person;
                if (Math.Abs(delta.y) > 2) continue;
                float distance = delta.x * delta.x + delta.z * delta.z;
                if (distance <= 12 * 12) helpers++;
                if (distance < nearest) { nearest = distance; origin = person; }
            }
            return helpers >= 2 ? nearest : float.MaxValue;
        }
    }
    internal static class DevRiotCrowdRules
    {
        internal static float SimulationAdvance(float maximum,float actual,float front)
        { return Math.Min(maximum,Math.Max(0,actual-front+.05f)); }
        internal static float X(int actor, float time)
        {
            return (float)(Math.Cos(actor * 2.399963) * Math.Sqrt((actor + .5) / 88) * 5.5 + Math.Sin(time * .23 + actor * 1.7) * .15);
        }
        internal static float Z(int actor, float time)
        {
            return (float)(Math.Sin(actor * 2.399963) * Math.Sqrt((actor + .5) / 88) * 8 + Math.Cos(time * .2 + actor * 2.3) * .18);
        }
        internal static bool Watching(int actor, float time)
        { return actor % 5 == 0 && (time + actor * 1.7f) % 11 < 1.6f; }
        internal static float WalkSpeed(int actor) { return 1.7f + actor % 5 * .16f; }
        internal static bool Resting(int actor, float time)
        { return (time + actor * 1.73f) % (13 + actor % 7) < .6f + actor % 4 * .25f; }
        internal static float Pace(int actor, float time)
        { return .95f + (float)Math.Sin(time * .19 + actor * 2.1) * .15f; }
        internal static float Advance(float station, float meters, float pace, bool waiting, float end)
        { return Math.Min(end, station + (waiting ? 0 : Math.Max(0, meters) * pace)); }
        internal static int Nearest(float[] distances, bool[] taken)
        {
            int result = -1; float nearest = 12 * 12;
            for (int i = 0; i < distances.Length; i++)
                if (!taken[i] && distances[i] >= 0 && distances[i] < nearest) { result = i; nearest = distances[i]; }
            return result;
        }
        internal static void ApproachWaypoint(float x, float z, float goalX, float goalZ, float halfWidth, float halfLength, bool rear, out float nextX, out float nextZ)
        {
            nextX = goalX; nextZ = goalZ;
            if (rear && z > -halfLength)
            {
                if (Math.Abs(x) < halfWidth + .1f) { nextX = x < 0 ? -halfWidth - .3f : halfWidth + .3f; nextZ = z; }
                else { nextX = x; nextZ = -halfLength - .3f; }
            }
            else if (!rear && Math.Abs(z) < halfLength && x * goalX < 0)
            { nextX = x; nextZ = z < 0 ? -halfLength - .3f : halfLength + .3f; }
            else if (!rear && Math.Abs(z) >= halfLength && Math.Abs(x - goalX) > .1f)
            { nextX = goalX; nextZ = z; }
        }
    }
    public sealed partial class CrowdPreview
    {
        private readonly Vector3[] devRiotPlaces = new Vector3[88];
        private readonly Quaternion[] devRiotFacings = new Quaternion[88];
        private readonly bool[] devRiotPlaced = new bool[88];
        private readonly bool[] devRiotBorrowed = new bool[88];
        private readonly float[] devRiotDistances = new float[88];
        private readonly float[] devStations = new float[88], devSides = new float[88], devSideGoals = new float[88], devDecisions = new float[88];
        private readonly bool[] devWalking = new bool[88];
        private Vector3[] devWalkRoute;
        private readonly DevVisualMotion devWalkMotion = new DevVisualMotion();
        private double devWalkHours;
        private bool devWalkStarted;
        private float devPublishAt;
        private readonly DevAttackBudget devAttackBudget = new DevAttackBudget();
        internal void DevCountAttack(int index) { devAttackBudget.Count(index); }
        private void DevResetRiotCrowd()
        {
            Array.Clear(devRiotPlaced, 0, devRiotPlaced.Length);
            devAttackBudget.Reset();
            Array.Clear(devRiotBorrowed,0,devRiotBorrowed.Length);
            devWalkStarted = false; devWalkMotion.Reset(); devWalkRoute = null; DevCrowdPresence.Current = null;
        }
        internal CrowdActor DevBorrowRiotActor(Vector3 target, out int index)
        {
            index = -1;
            DevRoadRibbon ribbon = CivicUnrest.View.DevRoad;
            if (ribbon == null || (ribbon.Constrain(target) - target).sqrMagnitude > 2 * 2) return null;
            if (failed || !CivicUnrest.View.Active || !CivicUnrest.View.Riot || devSuppressing) return null;
            for (int i = 0; i < devRiotDistances.Length; i++)
                devRiotDistances[i] = i < people.Count && devRiotPlaced[i] && !DevCombatUnavailable(i) && devAttackBudget.Available(i) ? (devRiotPlaces[i] - target).sqrMagnitude : float.MaxValue;
            index = DevRiotCrowdRules.Nearest(devRiotDistances, devRiotBorrowed);
            if (index < 0) return null;
            people[index].Pose(devRiotPlaces[index], devRiotFacings[index], true, false, false, animationTime, 0);
            devRiotBorrowed[index] = true;
            return people[index];
        }
        internal void DevReturnRiotActor(int index)
        {
            if (index < 0 || index >= people.Count) return;
            devRiotBorrowed[index] = false;
            if (people[index].Root == null) return;
            people[index].SetPlacardVisible(true);
            if (!CivicUnrest.View.Active || !CivicUnrest.View.Riot) { people[index].Show(false); return; }
            // Keep the same model and its last position; never jump back to its old marching slot.
            Transform actor = people[index].Root.transform;
            devRiotPlaces[index] = actor.position; devRiotFacings[index] = actor.rotation; devRiotPlaced[index] = true;
            if(devRioters[index]!=null) { devRioters[index].Position=actor.position; devRioters[index].MotionRoute=null; devNextLinePlan=0; }
            DevRoadRibbon ribbon = CivicUnrest.View.DevRoad;
            if (ribbon != null)
            {
                float station, side; ribbon.Project(actor.position, out station, out side);
                devStations[index] = station; devSides[index] = side;
            }
        }
        private void DevAdvanceWalkers(UnrestView state, float delta)
        {
            DevRoadRibbon ribbon = state.DevRoad;
            if (!state.Riot || ribbon == null) return;
            double hours = devWalkMotion.Follow(state.VisualClock / (double)GameClock.HourTicks, Time.unscaledDeltaTime, delta <= 0);
            float meters = !devWalkStarted ? 0 : (float)Math.Max(0, Math.Min(1, hours - devWalkHours)) * UnrestRules.Speed(true);
            devWalkHours = hours; devWalkStarted = true;
            bool changed = devWalkRoute != ribbon.Points; devWalkRoute = ribbon.Points;
            DevAssaultView assault = DevVehicleAssault.View;
            for (int index = 0; index < 88; index++)
            {
                if (devRiotBorrowed[index] || DevCombatControls(index)) continue;
                Vector3 forward;
                if (!devRiotPlaced[index])
                {
                    devStations[index] = Math.Max(16, ribbon.Front) + DevRiotCrowdRules.Z(index, animationTime) - 8;
                    devSides[index] = devSideGoals[index] = DevRiotCrowdRules.X(index, animationTime);
                    devRiotPlaces[index] = ribbon.At(devStations[index], devSides[index], out forward) + Vector3.up * .04f;
                    devRiotFacings[index] = Quaternion.LookRotation(forward); devRiotPlaced[index] = true;
                }
                else if (changed) ribbon.Project(devRiotPlaces[index], out devStations[index], out devSides[index]);
                if (animationTime >= devDecisions[index])
                {
                    devSideGoals[index] = (float)Math.Sin(index * 2.1 + Math.Floor(animationTime / (6 + index % 5))) * 5;
                    devDecisions[index] = animationTime + 6 + index % 5;
                }
                Vector3 before = devRiotPlaces[index];
                bool watching = assault != null && DevRiotCrowdRules.Watching(index, animationTime) && (assault.Position - before).sqrMagnitude < 100;
                bool hold = state.Police && (before - state.Position).sqrMagnitude < 32 * 32;
                bool waiting = hold || watching || DevRiotCrowdRules.Resting(index, animationTime);
                devStations[index] = DevRiotCrowdRules.Advance(devStations[index], meters, DevRiotCrowdRules.Pace(index, animationTime), waiting, Math.Min(ribbon.Limit, ribbon.Distances[ribbon.Distances.Length - 1]));
                if (!waiting)
                {
                    devSides[index] = Mathf.MoveTowards(devSides[index], devSideGoals[index], delta * .6f);
                }
                devRiotPlaces[index] = ribbon.At(devStations[index], devSides[index], out forward) + Vector3.up * .04f;
                Vector3 travel = devRiotPlaces[index] - before; travel.y = 0;
                devWalking[index] = delta > 0 && travel.sqrMagnitude > .0225f * delta * delta;
                if (watching) forward = assault.Position - before;
                forward=DevVisualMotion.LookDirection(travel,devWalking[index],forward);
                if (forward.sqrMagnitude > .001f) devRiotFacings[index] = Quaternion.Slerp(devRiotFacings[index], Quaternion.LookRotation(forward), DevVisualMotion.Blend(delta, 8));
            }
            if (Time.realtimeSinceStartup < devPublishAt) return;
            devPublishAt = Time.realtimeSinceStartup + .2f;
            List<Vector3> positions = new List<Vector3>(), damagePositions=new List<Vector3>(); List<int> cells = new List<int>(); List<int> largeCells=new List<int>();
            for (int i = 0; i < 88; i++) if (devRiotPlaced[i] && !DevCombatUnavailable(i) && !devRiotBorrowed[i])
            {
                Vector3 p = devRiotPlaces[i];
                if(devBattleOutcome<=0) damagePositions.Add(p);
                if(!devAttackBudget.Available(i)) continue;
                positions.Add(p);
                int x = Mathf.Clamp((int)(p.x / 32 + 270), 0, 539), z = Mathf.Clamp((int)(p.z / 32 + 270), 0, 539);
                for (int row = Math.Max(0, z - 1); row <= Math.Min(539, z + 1); row++)
                    for (int col = Math.Max(0, x - 1); col <= Math.Min(539, x + 1); col++)
                    { int cell = row * 540 + col; if (!cells.Contains(cell)) cells.Add(cell); }
                x=Mathf.Clamp((int)(p.x/320+27),0,53); z=Mathf.Clamp((int)(p.z/320+27),0,53);
                for(int row=Math.Max(0,z-1);row<=Math.Min(53,z+1);row++)
                    for(int col=Math.Max(0,x-1);col<=Math.Min(53,x+1);col++)
                    { int cell=row*54+col; if(!largeCells.Contains(cell)) largeCells.Add(cell); }
            }
            List<float> stations = new List<float>();
            for(int i=0;i<88;i++) if(devRiotPlaced[i] && !DevCombatUnavailable(i)) {
                float along=Mathf.Clamp(devStations[i],0,Math.Min(ribbon.Limit,ribbon.Distances[ribbon.Distances.Length-1]));
                // Ordinary walkers already own their route station. Only externally moved actors need projection.
                if(devRiotBorrowed[i] || DevCombatControls(i)) {
                    Vector3 actual=devRiotBorrowed[i] && i<people.Count && people[i].Root!=null ? people[i].Root.transform.position : devRiotPlaces[i];
                    float side; ribbon.Project(actual,out along,out side);
                }
                stations.Add(along);
            }
            stations.Sort(); Vector3 heading;
            Vector3 center=stations.Count==0 ? state.Position : ribbon.At(stations[stations.Count/2],0,out heading);
            DevCrowdPresence.Current = new DevCrowdPresence { Stamp = CivicUnrest.DevRiotStamp, Positions = positions.ToArray(), DamagePositions=damagePositions.ToArray(), Frame=ColossalFramework.Singleton<SimulationManager>.instance.m_currentFrameIndex, Cells = cells.ToArray(), LargeCells=largeCells.ToArray(), Center=center, HasCenter=stations.Count>0 };
        }
        private bool DevRiotPose(int index, Quaternion facing, float delta, DevAssaultView assault, out Vector3 position, out Quaternion turn)
        {
            position = devRiotPlaces[index]; turn = devRiotFacings[index]; return devWalking[index];
        }
    }
}
