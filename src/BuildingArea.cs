using System;
using System.Collections.Generic;
using UnityEngine;

namespace CivicPressure
{
    // Simulation-thread broad phase. Callers retain their exact radius, identity and service checks.
    internal static class BuildingArea
    {
        internal static void Fill(Building[] buildings,ushort[] grid,Vector3 minimum,Vector3 maximum,List<ushort> result)
        {
            result.Clear();
            int x0=Mathf.Clamp((int)(minimum.x/64+135),0,269), x1=Mathf.Clamp((int)(maximum.x/64+135),0,269);
            int z0=Mathf.Clamp((int)(minimum.z/64+135),0,269), z1=Mathf.Clamp((int)(maximum.z/64+135),0,269);
            for(int z=z0;z<=z1;z++) for(int x=x0;x<=x1;x++) {
                int cell=z*270+x; if(cell>=grid.Length) continue;
                ushort id=grid[cell];
                for(int guard=0;id!=0 && id<buildings.Length && guard<buildings.Length;guard++) {
                    result.Add(id); id=buildings[id].m_nextGridBuilding;
                }
            }
        }
    }
}
