using System;
using UnityEngine;

namespace CivicPressure
{
    internal static class DevBuildingDamage
    {
        internal static bool Fresh(DevCrowdPresence crowd,long stamp,uint frame)
        { return crowd!=null && crowd.Stamp==stamp && frame-crowd.Frame<=256 && crowd.DamagePositions!=null && crowd.DamagePositions.Length>0; }
        internal static bool Near(Vector3 building,Vector3[] people,Vector3[] road,float halfWidth)
        {
            if(people==null || road==null || road.Length<2) return false;
            float width=Math.Max(0,halfWidth)+2;
            foreach(Vector3 person in people) {
                Vector3 nearby=building-person;
                if(Math.Abs(nearby.y)>8 || nearby.x*nearby.x+nearby.z*nearby.z>64*64) continue;
                for(int i=1;i<road.Length;i++) {
                    Vector3 start=road[i-1], delta=road[i]-start;
                    float length=delta.x*delta.x+delta.z*delta.z;
                    float t=length<.0001f ? 0 : Mathf.Clamp01(((person.x-start.x)*delta.x+(person.z-start.z)*delta.z)/length);
                    Vector3 offset=person-(start+delta*t);
                    if(Math.Abs(offset.y)<=4 && offset.x*offset.x+offset.z*offset.z<=width*width) return true;
                }
            }
            return false;
        }
    }
}
