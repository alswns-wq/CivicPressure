using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;
namespace CivicPressure
{
    internal sealed class ComplaintHome
    {
        internal uint Citizen, BuildIndex;
        internal ushort Building;
        internal static bool Matches(uint expectedIndex,uint actualIndex,ushort expectedHome,ushort actualHome,bool live)
        { return live && expectedHome!=0 && expectedHome==actualHome && expectedIndex==actualIndex; }
        internal bool Position(out Vector3 position)
        {
            position=Vector3.zero;
            Citizen[] citizens=Singleton<CitizenManager>.instance.m_citizens.m_buffer;
            Building[] buildings=Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            if(Citizen==0 || Citizen>=citizens.Length || Building==0 || Building>=buildings.Length)return false;
            Citizen c=citizens[Citizen]; Building b=buildings[Building];
            bool live=(c.m_flags&(global::Citizen.Flags.Created|global::Citizen.Flags.Dead|global::Citizen.Flags.MovingIn))==global::Citizen.Flags.Created && CivicSimulation.Live(b);
            if(!Matches(BuildIndex,b.m_buildIndex,Building,c.m_homeBuilding,live))return false;
            position=b.m_position;return true;
        }
    }
    internal sealed class ComplaintSite
    {
        internal ushort Building;
        internal uint BuildIndex;
        internal int Reason;
        internal bool Position(out Vector3 position)
        {
            position=Vector3.zero;
            Building[] buildings=Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            if(Building==0 || Building>=buildings.Length)return false;
            Building b=buildings[Building];
            if(!CivicSimulation.Live(b) || b.m_buildIndex!=BuildIndex ||
                (ComplaintMapping.Harms(b.m_problems)&(1<<Reason))==0)return false;
            position=b.m_position;return true;
        }
    }
    internal sealed class ComplaintHotspot
    {
        internal int Region,Reason,Affected,Severe,Density;
        internal ushort Target,Line;
        internal uint BuildIndex;
        internal Vector3 Position;
        internal static bool Better(ComplaintHotspot a,ComplaintHotspot b)
        {
            if(b==null)return true;
            if(a.Reason==2 && a.Density!=b.Density)return a.Density>b.Density;
            return a.Affected+.5*a.Severe>b.Affected+.5*b.Severe;
        }
        internal bool Live()
        {
            NetManager net=Singleton<NetManager>.instance;
            if(Reason==17){
                if(Target==0 || Target>=net.m_nodes.m_buffer.Length)return false;
                NetNode n=net.m_nodes.m_buffer[Target];
                return (n.m_flags&(NetNode.Flags.Created|NetNode.Flags.Deleted))==NetNode.Flags.Created &&
                    n.m_buildIndex==BuildIndex && n.m_transportLine==Line && Line!=0;
            }
            if(Target==0 || Target>=net.m_segments.m_buffer.Length)return false;
            NetSegment road=net.m_segments.m_buffer[Target];
            return (road.m_flags&(NetSegment.Flags.Created|NetSegment.Flags.Deleted))==NetSegment.Flags.Created && road.m_buildIndex==BuildIndex;
        }
        internal string Label()
        {
            if(Reason==17){
                TransportLine[] lines=Singleton<TransportManager>.instance.m_lines.m_buffer;
                return L.T("혼잡 정류장 · 노선 ")+(Line<lines.Length ? lines[Line].m_lineNumber.ToString() : Line.ToString());
            }
            string name=Singleton<NetManager>.instance.GetSegmentName(Target);
            return string.IsNullOrEmpty(name) ? L.T("혼잡 도로 #")+Target : name;
        }
    }
    internal sealed class HotspotCensus
    {
        private readonly Dictionary<long,ComplaintHotspot> samples=new Dictionary<long,ComplaintHotspot>();
        internal IEnumerable<ComplaintHotspot> Values {get{return samples.Values;}}
        private void Add(int region,int reason,ushort target,uint index,ushort line,Vector3 position,bool severe,int density)
        {
            long key=((long)(region*ComplaintRules.Slots+reason)<<16)|target;ComplaintHotspot s;
            if(!samples.TryGetValue(key,out s)){
                s=new ComplaintHotspot{Region=region,Reason=reason,Target=target,BuildIndex=index,Line=line,Position=position};
                samples.Add(key,s);
            }
            s.Affected++;if(severe)s.Severe++;s.Density=Math.Max(s.Density,density);
        }
        internal int Waiting(int region,CitizenInstance instance,int severity)
        {
            if(severity<=0 || (instance.m_flags&CitizenInstance.Flags.TargetIsNode)==0)return -1;
            ushort id=instance.m_targetBuilding;NetNode[] nodes=Singleton<NetManager>.instance.m_nodes.m_buffer;
            if(id==0 || id>=nodes.Length)return -1;
            NetNode n=nodes[id];
            if((n.m_flags&(NetNode.Flags.Created|NetNode.Flags.Deleted))!=NetNode.Flags.Created || n.m_transportLine==0)return -1;
            int owner=Singleton<DistrictManager>.instance.GetDistrict(n.m_position); Add(owner,17,id,n.m_buildIndex,n.m_transportLine,n.m_position,severity>1,0); return owner;
        }
        internal int Traffic(int region,ushort home,ushort work,ushort visit,Building[] buildings)
        {
            NetSegment[] roads=Singleton<NetManager>.instance.m_segments.m_buffer;
            ushort best=0;int density=79;
            // A citizen counts once; choose the worst actual home/work/visit access road.
            for(int i=0;i<3;i++){
                ushort building=i==0?home:i==1?work:visit;
                if(building==0 || building>=buildings.Length || !CivicSimulation.Live(buildings[building]))continue;
                ushort id=buildings[building].m_accessSegment;
                if(id==0 || id>=roads.Length)continue;
                NetSegment r=roads[id];
                if((r.m_flags&(NetSegment.Flags.Created|NetSegment.Flags.Deleted))!=NetSegment.Flags.Created ||
                    r.Info==null || r.Info.m_class.m_service!=ItemClass.Service.Road || r.m_trafficDensity<=density)continue;
                best=id;density=r.m_trafficDensity;
            }
            if(best!=0) {
                Vector3 position=UnrestRoads.Curve(roads[best]).Position(.5f);
                int owner=Singleton<DistrictManager>.instance.GetDistrict(position); Add(owner,2,best,roads[best].m_buildIndex,0,position,density>=95,density); return owner;
            }
            return -1;
        }
    }
}
