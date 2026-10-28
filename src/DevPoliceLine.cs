using System;
using UnityEngine;

namespace CivicPressure
{
    internal static class DevPoliceLineRules
    {
        internal static float ParkingOffset(int slot,int attempt,int retries,bool twoWay,out int side)
        {
            side=twoWay && slot%2!=0 ? 1 : -1;
            if(twoWay && attempt>=64) side=-side;
            return side*(16+((twoWay ? slot/2 : slot)+retries+attempt%64)*14);
        }
        internal static float Clearance(float a,float b) { return Math.Max(7,(Math.Max(0,a)+Math.Max(0,b))*.5f+2); }
        internal static int Columns(float width) { return Math.Max(1,(int)(Math.Max(0,width)*2/1.2f)+1); }
        internal static float SlotSide(int slot,int count,float width)
        {
            int columns=Columns(width), row=slot/columns;
            int occupied=Math.Min(columns,Math.Max(1,count-row*columns));
            return (slot%columns-(occupied-1)*.5f)*1.2f;
        }
        internal static float SlotBack(int slot,float width) { return slot/Columns(width)*1.6f; }
        internal static int Weapon(float distance) { return distance<=3.5f ? 1 : distance<=22 ? 2 : 0; }
        internal static bool Pressure(int rioters,int police) { return police>0 && rioters>police; }
        internal static int ArrestTeams(int rioters,int police)
        { return rioters>0 && police*2>=rioters*3 ? Math.Min(3,police/2) : 0; }
        internal static bool CustodyStrike(float clock,float actionAt)
        { return clock>=actionAt && clock-actionAt<.7f; }
        internal static float ApproachGap(int index,bool pressure)
        { return Melee(index) ? (pressure ? 1.7f : 2.1f)+index%3*.15f : (pressure ? 7 : 9)+index%3; }
        internal static bool Melee(int index) { return index%5<2; }
        internal static int RiotWeapon(int index,bool gun,float distance)
        {
            if(gun) return distance<=22 ? 5 : 0;
            if(distance<3) return 1;
            return !Melee(index) && distance<=16 ? 2 : 0;
        }
        internal static float RiotInterval(int index,int action)
        { return action==5 ? 3.2f+index%5*.5f : (5+index%5*.5f)*(action==1 && Melee(index) ? .8f : 1); }
        internal static int RiotTarget(DevCombatant[] officers,Vector3 position,int[] assigned,int previous)
        {
            int target=-1; float best=float.MaxValue;
            for(int i=0;i<officers.Length;i++) {
                DevCombatant officer=officers[i];
                if(officer==null || officer.Dead || officer.Gone || officer.Hits>=officer.Limit) continue;
                float score=(officer.Position-position).sqrMagnitude+assigned[i]*64-(i==previous ? 8 : 0);
                if(score<best) { best=score; target=i; }
            }
            return target;
        }
        internal static float Advance(float station,float target,float delta)
        { return Mathf.MoveTowards(station,target,Math.Max(0,delta)*.6f); }
    }
    internal sealed class DevPoliceLineState
    {
        internal bool Ready, Advancing, Active;
        internal int Count;
        internal float Station, Goal, Width;
        internal Vector3[] Route;
        internal Vector3 Center;
        internal readonly GameObject[] Tape=new GameObject[3];
    }
    public sealed partial class CrowdPreview
    {
        private readonly DevPoliceLineState[] devLines={new DevPoliceLineState(),new DevPoliceLineState()};
        private float devNextLinePlan;
        private bool devPressure, devBarricadeReady, devVictoryMarch;
        private bool devArrestMode;
        private readonly bool[] devArrestTargets=new bool[88];
        private int[] devRiotTargetLoad=new int[25];
        private Vector3 devBarricadeCenter;
        private Vector3[] devBarricadeRoute;
        private float devBarricadeStation;
        private void DevResetLines()
        {
            devNextLinePlan=0;
            devPressure=devBarricadeReady=devVictoryMarch=devArrestMode=false; devBarricadeRoute=null;
            foreach(DevPoliceLineState line in devLines) {
                line.Ready=line.Active=false; line.Count=0; line.Route=null;
                for(int i=0;i<line.Tape.Length;i++) { if(line.Tape[i]!=null) Destroy(line.Tape[i]); line.Tape[i]=null; }
            }
        }
        private static void DevLocate(DevCombatant person,DevRoadRibbon road)
        {
            if(person.MotionRoute==road.Points) return;
            road.Project(person.Position,out person.Station,out person.Side); person.MotionRoute=road.Points;
        }
        private static void DevMoveSlot(DevCombatant person,DevRoadRibbon road,float station,float side,float meters)
        {
            DevLocate(person,road);
            station=Mathf.Clamp(station,0,Math.Min(road.Limit,road.Distances[road.Distances.Length-1]));
            float ds=station-person.Station, dx=side-person.Side;
            float length=(float)Math.Sqrt(ds*ds+dx*dx), fraction=length<.001f ? 0 : Math.Min(1,Math.Max(0,meters)/length);
            Vector3 before=person.Position, forward;
            person.Station+=ds*fraction; person.Side+=dx*fraction;
            person.Position=road.At(person.Station,person.Side,out forward);
            person.Travel=person.Position-before; person.Moving=person.Travel.sqrMagnitude>.000001f;
        }
        private void DevLineTick(DevRoadRibbon road,float delta)
        {
            bool planning=devBattleClock>=devNextLinePlan;
            if(planning) {
                int rioters=0, police=0;
                foreach(DevCombatant person in devRioters) if(person!=null && !person.Dead && !person.Gone && !person.Fleeing && !person.Detained) rioters++;
                foreach(DevCombatant person in devPolice) if(person!=null && !person.Gone && person.Hits<person.Limit) police++;
                devPressure=DevPoliceLineRules.Pressure(rioters,police);
                DevAssignArrestTeams(DevPoliceLineRules.ArrestTeams(rioters,police));
            }
            if(planning) devNextLinePlan=devBattleClock+.2f;
            for(int side=0;side<2;side++) {
                DevPoliceLineState line=devLines[side]; line.Active=false;
                foreach(DevCombatant officer in devPolice)
                    if(officer!=null && officer.Line==side && !officer.Gone && officer.Hits<officer.Limit) { line.Active=true; break; }
                if(!line.Active) continue;
                if(!line.Ready) { line.Station=road.Front+(side==0 ? -10 : 10); line.Goal=line.Station; line.Ready=true; }
                else if(line.Route!=road.Points) { float lateral; road.Project(line.Center,out line.Station,out lateral); line.Goal=line.Station; }
                line.Route=road.Points;
                float limit=Math.Min(road.Limit,road.Distances[road.Distances.Length-1]);
                line.Station=Mathf.Clamp(line.Station,0,limit);
                Vector3 forward;
                line.Center=road.At(line.Station,0,out forward);
                line.Width=Math.Max(.5f,(road.At(line.Station,100,out forward)-line.Center).magnitude-.5f);
                if(planning) {
                    float nearest=float.MaxValue, target=line.Station;
                    for(int i=0;i<devRioters.Length;i++) {
                        DevCombatant rioter=devRioters[i];
                        if(rioter==null || rioter.Dead || rioter.Gone || rioter.Fleeing || rioter.Detained || devRiotBorrowed[i] || DevCombatRules.Down(devBattleClock,rioter.DownUntil)) continue;
                        DevLocate(rioter,road); float gap=Math.Abs(rioter.Station-line.Station);
                        if(gap<nearest) { nearest=gap; target=rioter.Station; }
                    }
                    line.Goal=!devPressure && nearest>12 && nearest<float.MaxValue ? target+(target<line.Station ? 12 : -12) : line.Station;
                }
                line.Goal=Mathf.Clamp(line.Goal,0,limit);
                DevPoliceLineState other=devLines[1-side];
                if(other.Ready && other.Active) line.Goal=side==0 ? Math.Min(line.Goal,other.Station-6) : Math.Max(line.Goal,other.Station+6);
                float reserve=Math.Min(limit*.5f,DevPoliceLineRules.SlotBack(Math.Max(0,line.Count-1),line.Width));
                line.Goal=Mathf.Clamp(line.Goal,side==0 ? reserve : 0,side==1 ? limit-reserve : limit);
                line.Advancing=Math.Abs(line.Station-line.Goal)>.1f;
                if(devBattleOutcome==0) line.Station=DevPoliceLineRules.Advance(line.Station,line.Goal,delta);
                line.Center=road.At(line.Station,0,out forward);
                line.Width=Math.Max(.5f,(road.At(line.Station,100,out forward)-line.Center).magnitude-.5f);
            }
            if(planning) {
                foreach(DevCombatant officer in devPolice) if(officer!=null && !officer.Gone && officer.Hits<officer.Limit && !officer.ArrestTeam && officer.Prisoner<0)
                    officer.Target=DevNearest(devRioters,officer.Position,true);
                if(devRiotTargetLoad.Length<devPolice.Length) Array.Resize(ref devRiotTargetLoad,devPolice.Length);
                Array.Clear(devRiotTargetLoad,0,devRiotTargetLoad.Length);
                for(int i=0;i<devRioters.Length;i++) {
                    DevCombatant rioter=devRioters[i]; if(rioter==null || rioter.Dead || rioter.Gone || rioter.Fleeing || rioter.Detained || rioter.Pickup!=null || devRiotBorrowed[i] || DevCombatRules.Down(devBattleClock,rioter.DownUntil)) continue;
                    rioter.Target=DevPoliceLineRules.RiotTarget(devPolice,rioter.Position,devRiotTargetLoad,rioter.Target);
                    if(rioter.Target>=0) devRiotTargetLoad[rioter.Target]++;
                }
            }
        }
        private void DevAdvanceOfficer(DevCombatant person,DevRoadRibbon road,float delta)
        {
            if(person.ArrestTeam && person.Target>=0 && person.Target<devRioters.Length) {
                DevCombatant suspect=devRioters[person.Target];
                if(suspect!=null && !suspect.Dead && !suspect.Gone && !suspect.Fleeing && !suspect.Detained) {
                    DevLocate(suspect,road); DevLocate(person,road);
                    float gap=person.Station<suspect.Station ? -1.5f : 1.5f;
                    DevMoveSlot(person,road,suspect.Station+gap,suspect.Side,delta*2.2f);
                    person.Aim=suspect.Position; return;
                }
            }
            DevPoliceLineState line=devLines[person.Line]; int outward=person.Line==0 ? -1 : 1;
            float target=line.Station+outward*DevPoliceLineRules.SlotBack(person.LineSlot,line.Width);
            DevMoveSlot(person,road,target,DevPoliceLineRules.SlotSide(person.LineSlot,line.Count,line.Width),delta*1.8f);
            if(person.Target>=0 && person.Target<devRioters.Length && devRioters[person.Target]!=null) person.Aim=devRioters[person.Target].Position;
            else { Vector3 heading; road.At(line.Station,0,out heading); person.Aim=person.Position-heading*outward; }
        }
        private void DevAssignArrestTeams(int count)
        {
            devArrestMode=count>0; Array.Clear(devArrestTargets,0,devArrestTargets.Length);
            int used=0;
            foreach(DevCombatant officer in devPolice) if(officer!=null && officer.Prisoner>=0) used++;
            // Keep a valid assignment through temporary knockdown instead of switching every 0.2s.
            foreach(DevCombatant officer in devPolice) if(officer!=null) {
                int target=officer.Target;
                bool keep=!officer.Armed && officer.ArrestTeam && !officer.Gone && officer.Hits<officer.Limit && officer.Prisoner<0 &&
                    used<count && DevArrestAvailable(target) && !devArrestTargets[target];
                officer.ArrestTeam=keep;
                if(keep) { devArrestTargets[target]=true; used++; }
            }
            foreach(DevCombatant officer in devPolice) {
                if(used>=count) break;
                if(officer==null || officer.Armed || officer.Gone || officer.Hits>=officer.Limit || officer.Prisoner>=0 || officer.ArrestTeam) continue;
                int target=-1; float nearest=float.MaxValue;
                for(int i=0;i<devRioters.Length;i++) {
                    DevCombatant person=devRioters[i];
                    if(!DevArrestAvailable(i) || devArrestTargets[i]) continue;
                    float distance=(person.Position-officer.Position).sqrMagnitude;
                    if(distance<nearest) { nearest=distance; target=i; }
                }
                if(target<0) break;
                devArrestTargets[target]=true; officer.ArrestTeam=true; officer.Target=target; used++;
            }
        }
        private bool DevArrestAvailable(int index)
        {
            if(index<0 || index>=devRioters.Length || devRiotBorrowed[index]) return false;
            DevCombatant person=devRioters[index];
            return person!=null && !person.Dead && !person.Gone && !person.Fleeing && !person.Detained;
        }
        private static void DevTakePrisoner(DevCombatant officer,DevCombatant person,int index)
        {
            officer.Prisoner=index; officer.BoardingAt=-1; officer.ExitRoute=null;
            person.Detained=true; person.Moving=false; person.Action=0; person.DownUntil=0;
            person.ReactionAt=person.FallAt=-100; person.BoardingAt=-1;
        }
        private void DevReleasePrisoner(DevCombatant officer,UnrestView state)
        {
            int index=officer.Prisoner; officer.Prisoner=-1; officer.BoardingAt=-1;
            if(index<0 || index>=devRioters.Length || devRioters[index]==null) return;
            DevCombatant person=devRioters[index]; person.Detained=false; person.BoardingAt=-1;
            if(!person.Gone && devBattleOutcome>0) DevStartFlee(person,state,index);
            else if(!person.Gone && devBattleOutcome==0) { DevCombatResult.Resolving=false; devNextLinePlan=0; }
        }
        private bool DevEscort(DevCombatant officer,DevRoadRibbon road,float delta)
        {
            DevCombatant person=devRioters[officer.Prisoner];
            if(person==null || person.Gone || !person.Detained) { officer.Prisoner=-1; return true; }
            if(DevPoliceLineRules.CustodyStrike(devBattleClock,officer.ActionAt)) {
                officer.Moving=person.Moving=false; officer.Aim=person.Position; person.Aim=officer.Position;
                return true;
            }
            if(officer.ExitRoute!=road.Points) {
                road.Project(officer.Exit,out officer.ExitStation,out officer.ExitSide); officer.ExitRoute=road.Points;
            }
            // A broken road must not leave a captive waiting forever for an unreachable car.
            if(officer.ExitStation>road.Limit+.5f) return false;
            DevMoveSlot(officer,road,officer.ExitStation,officer.ExitSide,delta*1.6f);
            officer.Aim=officer.Moving ? officer.Position+officer.Travel : person.Position;
            DevMoveSlot(person,road,officer.Station,officer.Side+.65f,delta*2.2f);
            person.Aim=officer.Position+officer.Travel;
            devRiotPlaces[officer.Prisoner]=person.Position;
            if((officer.Position-officer.Exit).sqrMagnitude>4 || (person.Position-officer.Exit).sqrMagnitude>6.25f) return true;
            if(officer.BoardingAt<0) { officer.BoardingAt=devBattleClock; person.BoardingAt=devBattleClock; }
            if(devBattleClock-officer.BoardingAt>=1.4f) {
                person.Gone=true; person.Detained=false; person.Moving=false;
                officer.Prisoner=-1; officer.BoardingAt=-1; officer.ArrestTeam=false; devNextLinePlan=0;
            }
            return true;
        }
        private void DevJoinLine(int index,DevCombatant person,DevRoadRibbon road,float delta)
        {
            if(person.Target<0 || person.Target>=devPolice.Length) return;
            DevCombatant opponent=devPolice[person.Target]; if(opponent==null || opponent.Dead || opponent.Gone || opponent.Hits>=opponent.Limit) return;
            DevPoliceLineState line=devLines[opponent.Line]; if(!line.Active) return;
            DevLocate(person,road); DevLocate(opponent,road);
            float direction=person.Station<opponent.Station ? -1 : 1;
            float gap=person.Weapon!=null ? 10+index%3 : DevPoliceLineRules.ApproachGap(index,devPressure);
            // Approach the assigned officer, not an unrelated position at the road's edge.
            bool melee=person.Weapon==null && DevPoliceLineRules.Melee(index);
            float lateral=opponent.Side+(index%3-1)*(melee ? .65f : 1.2f);
            float speed=melee && Math.Abs(person.Station-opponent.Station)<6 ? 3.1f : 2.1f;
            DevMoveSlot(person,road,opponent.Station+direction*gap,lateral,delta*speed);
            person.Aim=opponent.Position;
        }
        private void DevAnchorBarricade(DevRoadRibbon road)
        {
            Vector3 forward;
            if(!devBarricadeReady) {
                devBarricadeStation=road.Front;
                devBarricadeCenter=road.At(devBarricadeStation,0,out forward);
                devBarricadeRoute=road.Points; devBarricadeReady=true;
            } else if(devBarricadeRoute!=road.Points) {
                float side; road.Project(devBarricadeCenter,out devBarricadeStation,out side);
                devBarricadeRoute=road.Points;
            }
        }
        private void DevVictoryPatrol(int index,DevCombatant person,DevRoadRibbon road,float delta)
        {
            DevAnchorBarricade(road); Vector3 forward;
            // Fixed world anchor: the published crowd centre must not drag the patrol away.
            float phase=devBattleClock*.12f+index*2.399963f;
            float station=devBarricadeStation+(float)Math.Sin(phase)*(8+index%9);
            Vector3 center=road.At(station,0,out forward);
            float width=Math.Max(0,(road.At(station,100,out forward)-center).magnitude-.5f);
            DevMoveSlot(person,road,station,(float)Math.Sin(phase*.73f+index)*width,delta*1.2f);
            if(person.Moving) person.Aim=person.Position+person.Travel;
        }
        private static void DevVictoryAdvance(DevCombatant person,DevRoadRibbon road,float meters)
        {
            DevLocate(person,road);
            DevMoveSlot(person,road,person.Station+Math.Max(0,meters),person.Side,meters);
            if(person.Moving) person.Aim=person.Position+person.Travel;
        }
        private void DevHideLines()
        { foreach(DevPoliceLineState line in devLines) foreach(GameObject item in line.Tape) if(item!=null && item.activeSelf) item.SetActive(false); }
        private void DevShowLines(DevRoadRibbon road)
        {
            if(road==null) { DevHideLines(); return; }
            for(int side=0;side<2;side++) {
                DevPoliceLineState line=devLines[side];
                bool show=devSuppressing && devBattleOutcome==0 && !devArrestMode && line.Active && !line.Advancing;
                if(show && line.Tape[0]==null) {
                    line.Tape[0]=DevProp("CivicPressure_PoliceTape",Vector3.one,new Color(1,.82f,.08f),PrimitiveType.Cube);
                    for(int i=1;i<3;i++) line.Tape[i]=DevProp("CivicPressure_TapePost",new Vector3(.07f,.55f,.07f),new Color(.16f,.16f,.16f),PrimitiveType.Cylinder);
                }
                foreach(GameObject item in line.Tape) if(item!=null && item.activeSelf!=show) item.SetActive(show);
                if(!show || line.Tape[0]==null) continue;
                Vector3 heading; Vector3 center=road.At(line.Station+(side==0 ? 1 : -1),0,out heading);
                Quaternion facing=Quaternion.LookRotation(heading); Vector3 right=facing*Vector3.right;
                line.Tape[0].transform.position=center+Vector3.up*.95f;
                line.Tape[0].transform.rotation=facing;
                line.Tape[0].transform.localScale=new Vector3(line.Width*2,.12f,.025f);
                for(int i=1;i<3;i++) if(line.Tape[i]!=null) line.Tape[i].transform.position=center+right*((i==1 ? -1 : 1)*line.Width)+Vector3.up*.55f;
            }
        }
    }
}
