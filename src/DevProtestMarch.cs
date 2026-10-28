using System;
using UnityEngine;

namespace CivicPressure
{
    internal static class DevProtestRules
    {
        internal static float Cadence(int index) { return .88f+(index*7%13)*.02f; }
        internal static float Lift(int index,float time)
        { return DevVisualMotion.Gesture(index,time); }
        internal static Vector3 Position(DevRoadRibbon road,int index,float front,out Vector3 forward)
        {
            float end=Math.Max(0,Math.Min(road.Limit,road.Distances[road.Distances.Length-1]));
            float span=Math.Min(6,end*.5f), anchor=Mathf.Clamp(front,span,end-span);
            // Stable personal offsets; small changes depend on march progress, so a stopped group stays put.
            float offset=(index/7-3)*1.5f+(float)Math.Sin(index*2.4f)*.75f+
                (float)Math.Sin(front*.12f+index*1.7f)*.3f;
            float station=anchor+offset*(span/6);
            Vector3 middle=road.At(station,0,out forward);
            float width=Math.Min(5,Math.Max(0,(road.At(station,100,out forward)-middle).magnitude-.3f));
            float side=((index%7-3)+(float)Math.Sin(index*1.9f)*.4f+
                (float)Math.Sin(front*.08f+index)*.15f)/3.6f*width;
            return road.At(station,side,out forward)+Vector3.up*.04f;
        }
    }
    public sealed partial class CrowdPreview
    {
        private DevRoadRibbon devProtestRoad;
        private float devProtestFront;
        private readonly Vector3[] devProtestPlaces=new Vector3[49];
        private readonly Quaternion[] devProtestFacings=new Quaternion[49];
        private readonly bool[] devProtestPlaced=new bool[49];
        private void DevResetProtest()
        { devProtestRoad=null; devProtestFront=0; Array.Clear(devProtestPlaced,0,devProtestPlaced.Length); }
        private void DevProtestTick(DevRoadRibbon road,float delta)
        {
            if(road==null) { devProtestRoad=null; return; }
            if(devProtestRoad==null) devProtestFront=road.Front;
            else if(devProtestRoad.Points!=road.Points) {
                Vector3 heading; float side;
                road.Project(devProtestRoad.At(devProtestFront,0,out heading),out devProtestFront,out side);
            }
            devProtestRoad=road;
            devProtestFront=Mathf.Lerp(devProtestFront,road.Front,DevVisualMotion.Blend(delta,12));
        }
        private bool DevProtestPose(int index,float delta,out Vector3 position,out Quaternion facing,out bool moving)
        {
            position=devProtestPlaces[index]; facing=devProtestFacings[index]; moving=false;
            if(devProtestRoad==null) return devProtestPlaced[index];
            Vector3 forward;
            Vector3 next=DevProtestRules.Position(devProtestRoad,index,devProtestFront,out forward);
            moving=devProtestPlaced[index] && delta>0 && (next-position).sqrMagnitude>.000001f;
            facing=devProtestPlaced[index] ? Quaternion.Slerp(facing,Quaternion.LookRotation(forward),DevVisualMotion.Blend(delta,8)) : Quaternion.LookRotation(forward);
            position=next; devProtestPlaces[index]=next; devProtestFacings[index]=facing; devProtestPlaced[index]=true;
            return true;
        }
    }
}
