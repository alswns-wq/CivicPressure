using System;
using System.Collections.Generic;
using UnityEngine;

namespace CivicPressure
{
    internal struct DevCombatMotion
    {
        internal float Forward, Side, Lean, Roll, Fall, Kneel, Brace, Twist;
        private static float Pulse(float age,float length)
        { return age<=0 || age>=length ? 0 : (float)Math.Sin(age/length*Math.PI); }
        internal static DevCombatMotion Sample(float clock,int index,int action,float actionAt,float reactionAt,bool hit,float fallAt,float downUntil,bool permanent,bool moving)
        {
            DevCombatMotion pose=new DevCombatMotion();
            float age=clock-actionAt, reaction=Pulse(clock-reactionAt,.8f), side=index%2==0 ? 1 : -1;
            float strike=action==1 ? Pulse(age,.75f) : 0;
            float gas=action==3 && age>=0 && age<1 ? (float)Math.Sin(clock*5+index)*.3f : 0;
            pose.Forward=strike*.65f-(hit ? reaction*.45f : 0);
            pose.Side=(!hit ? reaction*.55f*side : 0)+gas*.3f;
            pose.Lean=(moving ? 9 : 0)+strike*24-(hit ? reaction*25 : 0);
            pose.Roll=(!hit ? reaction*20*side : 0)+gas*15;
            pose.Kneel=(!hit ? reaction*.16f : reaction*.08f);
            pose.Brace=reaction;
            pose.Twist=(action==1 ? DevVisualMotion.Strike(age,.75f)*22 : action==2 ? DevVisualMotion.Strike(age,1)*15 : 0)*side;
            if(fallAt>=0 && clock>=fallAt && (permanent || clock<downUntil)) {
                float landing=Mathf.SmoothStep(0,1,Mathf.Clamp01((clock-fallAt)/.4f));
                float recovery=permanent ? 0 : Mathf.SmoothStep(0,1,Mathf.Clamp01(clock-(downUntil-1)));
                pose.Fall=landing*(1-recovery);
                pose.Kneel=(float)Math.Sin(recovery*Math.PI)*.22f;
                pose.Forward=pose.Side=pose.Lean=pose.Roll=pose.Twist=0; pose.Brace=pose.Fall;
            }
            return pose;
        }
    }
    // Session-only cloned participants. This does not injure unrelated native citizens.
    internal sealed class DevCombatant
    {
        internal Vector3 Position, Aim, Exit;
        internal Vector3[] ExitRoute;
        internal float ExitStation, ExitSide, ExitLimit;
        internal bool ExitApproach;
        internal Vector3 Travel;
        internal Vector3[] MotionRoute;
        internal float Station, Side;
        internal int Line, LineSlot, Target=-1;
        internal bool Moving;
        internal float ReactionAt=-100, FallAt=-100;
        internal bool ReactionHit;
        internal bool ArrestTeam, Detained;
        internal int Prisoner=-1;
        internal float BoardingAt=-1;
        internal ushort Car;
        internal int Hits, Limit, Action;
        internal float DownUntil, NextAttack, ActionAt, FleeAt;
        internal bool Placed, Fleeing, Gone, Dead, Evacuated; internal DevTreatment Treatment;
        internal int Recoveries;
        internal bool Wounded, Armed;
        internal bool DeathRecorded;
        internal readonly IncidentCasualty Casualty=new IncidentCasualty(CivicRecords.Draft);
        internal DevLootWeapon Weapon, Pickup;
        internal bool MarkWounded() { if(Wounded) return false; Wounded=true; return true; }
        internal void FinishTreatment(bool fighting,float clock)
        {
            if(Treatment==null || !Treatment.Complete) return;
            Treatment=null;
            if(Dead || !fighting || Recoveries>=2) { Evacuated=true; return; }
            Recoveries++;
            Hits=0; FallAt=ReactionAt=-100; DownUntil=0;
            Action=0; ActionAt=-100; NextAttack=clock+2;
            ReactionHit=false; Moving=false; MotionRoute=null;
        }
    }
    public sealed partial class CrowdPreview
    {
        private readonly DevCombatant[] devRioters=new DevCombatant[88]; private DevCombatant[] devPolice=new DevCombatant[25];
        private readonly Dictionary<ushort,int> devBattleCars=new Dictionary<ushort,int>();
        private bool[] devPresentCars=new bool[5];
        private readonly System.Random devBattleRandom=new System.Random();
        private ushort devBattleLead;
        private float devBattleClock, devNextDecision, devNextGas, devEndAt;
        private int devBattleOutcome;
        private double devFleeHours;
        private bool devFleeStarted;
        private DevBackupRequest devBackup;
        private int devTotalOfficers, devWoundedOfficers;
        internal static bool DevNeedsBackup(int wounded,int total) { return total>0 && wounded*2>total; }
        private readonly List<GameObject> devBarricades=new List<GameObject>();
        private readonly List<Material> devBattleMaterials=new List<Material>();
        private readonly GameObject[] devProjectiles=new GameObject[25];
        private bool DevCombatUnavailable(int index)
        { return devRioters[index]!=null && (devRioters[index].Dead || devRioters[index].Gone || devRioters[index].Fleeing || devRioters[index].Detained || DevCombatRules.Down(devBattleClock,devRioters[index].DownUntil)); }
        private bool DevCombatControls(int index)
        { return devRioters[index]!=null && (devSuppressing || devRioters[index].Dead || devRioters[index].Fleeing || devRioters[index].Detained || devRioters[index].Gone || devBattleOutcome<0); }
        private void DevResetCombat()
        {
            DevResetDeaths();
            DevResetMedics(); devBackup=null; devTotalOfficers=devWoundedOfficers=0;
            DevResetLines();
            Array.Clear(devRioters,0,88); Array.Clear(devPolice,0,devPolice.Length); devBattleCars.Clear();
            devBattleClock=devNextDecision=devNextGas=devEndAt=0; devBattleLead=0; devBattleOutcome=0; devFleeHours=0; devFleeStarted=false;
            DevCombatResult.Current=null; DevCombatResult.Resolving=false;
#if DEV
            DevCombatResult.Requested=null;
#endif
            foreach(GameObject item in devBarricades) if(item!=null) Destroy(item); devBarricades.Clear();
            foreach(GameObject item in devProjectiles) if(item!=null) Destroy(item); Array.Clear(devProjectiles,0,25);
            foreach(Material material in devBattleMaterials) if(material!=null) Destroy(material); devBattleMaterials.Clear();
        }
        private void DevCombatTick(UnrestView state,float delta)
        {
            long stamp=CivicUnrest.DevRiotStamp;
            if(devRiotStamp!=stamp) { DevSuppressionReset(); devRiotStamp=stamp; }
            devBattleClock+=delta; devTime=devBattleClock; devCenter=state.Position;
            DevMedicTick(delta);
            // Walkers already interpolate the published game clock, including pause/speed changes.
            float fleeMeters=!devFleeStarted ? 0 : (float)Math.Max(0,Math.Min(1,devWalkHours-devFleeHours))*300;
            devFleeHours=devWalkHours; devFleeStarted=true;
            DevPoliceUnitView[] arrived=state.DevPoliceUnits;
            bool hasPolice=state.Police && arrived!=null && arrived.Length>0;
            if(hasPolice && !devSuppressing && devBattleOutcome<=0)
            {
                devSuppressing=true; devBattleLead=state.PoliceVehicle; devBattleOutcome=0; devEndAt=0;
                DevResetLines();
                devBattleCars.Clear(); Array.Clear(devPolice,0,devPolice.Length); devNextGas=devBattleClock+2;
                for(int i=0;i<88;i++) if(devRioters[i]!=null && !devRioters[i].Dead && !devRioters[i].Fleeing) { devRioters[i].Position=devRiotPlaces[i]; devRioters[i].Detained=false; }
                foreach(GameObject item in devBarricades) if(item!=null) Destroy(item); devBarricades.Clear();
                DevCombatResult.Current=null; DevCombatResult.Resolving=false;
            }
            if(!hasPolice) {
                devSuppressing=false; DevHideSuppressionEffects();
                foreach(DevCombatant officer in devPolice) if(officer!=null && officer.Prisoner>=0) DevReleasePrisoner(officer,state);
                foreach(GameObject projectile in devProjectiles) if(projectile!=null) projectile.SetActive(false);
            }
            if(hasPolice) devBattleLead=state.PoliceVehicle;
            // Once dispersal starts it must finish even if the last patrol car leaves.
            if(!devSuppressing && devBattleOutcome==0) return;
            DevRoadRibbon road=state.DevRoad;
            if(road==null) return;
            if(devBattleOutcome<0 && !hasPolice && DevVehicleAssault.NoTargets(stamp)) devVictoryMarch=true;
            Array.Clear(devPresentCars,0,devPresentCars.Length);
            if(hasPolice) foreach(DevPoliceUnitView unit in arrived)
            {
                int slot;
                if(!devBattleCars.TryGetValue(unit.Vehicle,out slot))
                {
                    if(devPolice.Length<(devBattleCars.Count+1)*5) {
                        Array.Resize(ref devPolice,(devBattleCars.Count+1)*5);
                        Array.Resize(ref devPresentCars,devBattleCars.Count+1);
                    }
                    slot=devBattleCars.Count; devBattleCars.Add(unit.Vehicle,slot);
                    int crew=DevCombatRules.Crew(unit.Vehicle);
                    for(int seat=0;seat<crew;seat++) {
                        Vector3 p=road.Constrain(unit.Position+new Vector3((seat%2==0?-1:1)*.8f,0,seat*.45f));
                        int line=unit.Side>0 ? 1 : 0;
                        // Two initial officers per riot; later waves do not replenish this allowance.
                        devPolice[slot*5+seat]=new DevCombatant { Armed=unit.Armed || devTotalOfficers+seat<2, Car=unit.Vehicle, Position=p, Exit=p, Placed=true, Line=line, LineSlot=devLines[line].Count++, Limit=8+seat%3, NextAttack=devBattleClock+1+seat*.3f };
                    }
                    devTotalOfficers+=crew;
                }
                devPresentCars[slot]=true;
            }
            for(int i=0;i<88;i++) if(devRioters[i]==null && devRiotPlaced[i])
                devRioters[i]=new DevCombatant { Position=devRiotPlaces[i], Placed=true, Limit=DevCombatRules.Resistance(i), NextAttack=devBattleClock+(float)devBattleRandom.NextDouble()*5 };
#if DEV
            DevCombatResult requested=DevCombatResult.Requested;
            if(requested!=null) {
                DevCombatResult.Requested=null;
                if(devBattleOutcome==0 && hasPolice && DevCombatResult.Matches(requested,stamp,devBattleLead)) {
                    DevCombatResult.Resolving=true; devBattleOutcome=requested.PoliceWon ? 1 : -1; devEndAt=devBattleClock+8;
                    if(requested.PoliceWon) {
                        for(int i=0;i<devRioters.Length;i++) if(devRioters[i]!=null && !devRioters[i].Gone && !devRioters[i].Fleeing && !devRioters[i].Detained)
                            DevStartFlee(devRioters[i],state,i);
                    } else {
                        foreach(DevCombatant officer in devPolice) if(officer!=null && !officer.Dead && !officer.Gone) { officer.Hits=officer.Limit; officer.FallAt=devBattleClock; }
                    }
                    CivicUnrest.DevStatus=requested.PoliceWon ? "[DEV] 경찰 승리 시험 · 폭동대 해산" : "[DEV] 시위대 승리 시험 · 경찰 철수";
                }
            }
#endif
            foreach(DevCombatant officer in devPolice) if(officer!=null) {
                int carSlot;
                bool present=devBattleCars.TryGetValue(officer.Car,out carSlot) && devPresentCars[carSlot];
                officer.FinishTreatment(present && devBattleOutcome==0,devBattleClock);
                officer.Gone=!present || officer.Evacuated; officer.Moving=false;
            }
            foreach(DevCombatant officer in devPolice) if(officer!=null && officer.Prisoner>=0 && (officer.Gone || officer.Hits>=officer.Limit || devBattleOutcome<0))
                DevReleasePrisoner(officer,state);
            if(devBattleOutcome==0 && hasPolice) DevLineTick(road,delta);
            if(devBattleOutcome<=0) DevPlanLoot(road);
            // Movement/state continues offscreen. Poses and particles alone are distance culled.
            int resisting=0, alive=0, remaining=0, fleeing=0;
            for(int i=0;i<88;i++)
            {
                DevCombatant person=devRioters[i]; if(person==null) { resisting++; remaining++; continue; }
                person.Moving=false;
                if(person.Gone) continue;
                if(person.Dead) { DevRecordDeath(person,false,i); continue; }
                remaining++;
                if(person.Detained) continue;
                if(person.Fleeing) { fleeing++; DevFlee(person,road,fleeMeters); devRiotPlaces[i]=person.Position; continue; }
                resisting++;
                if(devRiotBorrowed[i]) { if(person.Pickup!=null) person.Pickup.Release(); person.Position=devRiotPlaces[i]; person.MotionRoute=null; continue; }
                if(DevCombatRules.Down(devBattleClock,person.DownUntil)) continue;
                if(devGrenadeThrown && devBattleClock>=devThrowTime+2 && devBattleClock<devThrowTime+9 && (person.Position-devGasLanding).sqrMagnitude<8*8) {
                    if(person.Pickup!=null) person.Pickup.Release();
                    Vector3 away=person.Position-devGasLanding; away.y=0;
                    if(away.sqrMagnitude<.1f) away=Vector3.forward;
                    Vector3 moved=road.Constrain(person.Position+away.normalized*delta*3.5f);
                    if((moved-person.Position).sqrMagnitude<.0001f) {
                        float along,side,gasAlong,gasSide; Vector3 heading;
                        road.Project(person.Position,out along,out side); road.Project(devGasLanding,out gasAlong,out gasSide);
                        moved=road.At(along+(along<gasAlong ? -1 : 1)*delta*3.5f,side,out heading);
                    }
                    person.Travel=moved-person.Position; person.Moving=person.Travel.sqrMagnitude>.000001f;
                    person.Position=moved; person.MotionRoute=null; person.Action=3; person.ActionAt=devBattleClock;
                }
                else if(DevPickupWeapon(person,road,delta)) { }
                else if(devBattleOutcome==0 && hasPolice) DevJoinLine(i,person,road,delta);
                else if(devBattleOutcome<0) {
                    if(devVictoryMarch) DevVictoryAdvance(person,road,fleeMeters*(UnrestRules.Speed(true)/300f));
                    else DevVictoryPatrol(i,person,road,delta);
                }
                devRiotPlaces[i]=person.Position;
            }
            for(int i=0;i<devPolice.Length;i++) {
                DevCombatant officer=devPolice[i]; if(officer==null) continue;
                if(officer.Gone) continue;
                if(officer.Hits>=officer.Limit) {
                    if(!officer.Dead && devBattleOutcome<0 && devBattleClock>=devEndAt-5) {
                        Vector3 before=officer.Position;
                        officer.Position=DevCombatMove(road,officer.Position,officer.Exit,delta*1.8f);
                        officer.Travel=officer.Position-before; officer.Moving=officer.Travel.sqrMagnitude>.000001f;
                        officer.Aim=officer.Exit;
                    }
                    continue;
                }
                alive++;
                if(officer.Prisoner>=0) { if(!DevEscort(officer,road,delta)) DevReleasePrisoner(officer,state); continue; }
                if(devBattleOutcome==0) DevAdvanceOfficer(officer,road,delta);
            }
            if(devBattleOutcome==0 && hasPolice && delta>0 && devBattleClock>=devNextDecision)
            {
                devNextDecision=devBattleClock+.2f;
                for(int i=0;i<devPolice.Length;i++) {
                    DevCombatant officer=devPolice[i]; if(officer==null || officer.Gone || officer.Prisoner>=0 || officer.Hits>=officer.Limit || devBattleClock<officer.NextAttack) continue;
                    int target=officer.ArrestTeam ? officer.Target : DevNearest(devRioters,officer.Position,true); if(target<0 || target>=devRioters.Length || DevCombatUnavailable(target) || devRiotBorrowed[target]) continue;
                    DevCombatant rioter=devRioters[target]; float distance=Vector3.Distance(officer.Position,rioter.Position);
                    int weapon=DevPoliceLineRules.Weapon(distance); if(weapon==0) continue;
                    if(officer.ArrestTeam && weapon!=1) continue;
                    officer.Action=weapon; officer.ActionAt=devBattleClock; officer.Aim=rioter.Position;
                    officer.NextAttack=devBattleClock+(officer.Action==1 ? 1.8f : 3.2f)+i%3*.2f;
                    bool hit=devBattleRandom.NextDouble()<(officer.Action==1 ? .8 : .65);
                    if(weapon==2 || hit) DevPoliceSound(officer.Position,weapon==2,officer.Armed);
                    rioter.ReactionAt=devBattleClock; rioter.ReactionHit=hit;
                    if(hit) {
                        rioter.Casualty.Record(false,false);
                        if(officer.Armed && weapon==2 && DevCombatRules.LiveRound(rioter,devBattleClock,devBattleRandom.NextDouble())) {
                            DevRecordDeath(rioter,false,target);
                            resisting--; continue;
                        }
                        if(officer.ArrestTeam && weapon==1) { DevTakePrisoner(officer,rioter,target); resisting--; continue; }
                        if(officer.Action==2) rioter.FallAt=devBattleClock;
                        if(DevCombatRules.Force(rioter,devBattleClock,officer.Action==2)) DevStartFlee(rioter,state,target);
                    }
                }
                for(int i=0;i<88;i++) {
                    DevCombatant rioter=devRioters[i]; if(rioter==null || rioter.Dead || rioter.Gone || rioter.Detained || rioter.Fleeing || rioter.Pickup!=null || devRiotBorrowed[i] || DevCombatRules.Down(devBattleClock,rioter.DownUntil) || devBattleClock<rioter.NextAttack) continue;
                    int target=rioter.Target;
                    if(target<0 || target>=devPolice.Length || devPolice[target]==null || devPolice[target].Dead || devPolice[target].Gone || devPolice[target].Hits>=devPolice[target].Limit) continue;
                    DevCombatant officer=devPolice[target]; float distance=Vector3.Distance(rioter.Position,officer.Position);
                    bool gun=rioter.Weapon!=null;
                    int action=DevPoliceLineRules.RiotWeapon(i,gun,distance); if(action==0) continue;
                    rioter.Aim=officer.Position; rioter.Action=action; rioter.ActionAt=devBattleClock;
                    rioter.NextAttack=devBattleClock+DevPoliceLineRules.RiotInterval(i,action);
                    bool gas=devGrenadeThrown && devBattleClock<devThrowTime+9 && (rioter.Position-devGasLanding).sqrMagnitude<64;
                    bool hit=devBattleRandom.NextDouble()<(gas ? .03 : gun ? .65 : rioter.Action==1 ? .25 : .10);
                    if(gun) DevPoliceSound(rioter.Position,true,rioter.Weapon.Live);
                    officer.ReactionAt=devBattleClock; officer.ReactionHit=hit;
                    if(hit) DevHitOfficer(officer,target,gun,gun && rioter.Weapon.Live);
                }
                if(devBattleClock>=devNextGas) {
                    for(int i=0;i<devPolice.Length;i++) {
                        DevCombatant officer=devPolice[i]; if(officer==null || officer.Gone || officer.ArrestTeam || officer.Prisoner>=0 || officer.Hits>=officer.Limit) continue;
                        int target=DevNearest(devRioters,officer.Position,true); if(target<0) break;
                        float range=Vector3.Distance(officer.Position,devRioters[target].Position);
                        if(range<=3.5f || range>22) continue;
                        devForward=(devRioters[target].Position-officer.Position).normalized;
                        devThrowOrigin=officer.Position+Vector3.up*1.4f;
                        devGasLanding=road.Constrain(officer.Position+devForward*Math.Min(18,Vector3.Distance(officer.Position,devRioters[target].Position)));
                        devThrowTime=devBattleClock; devGrenadeThrown=true; officer.Action=4; officer.ActionAt=devBattleClock;
                        break;
                    }
                    devNextGas=devBattleClock+4;
                }
                int pending=Math.Max(0,state.PoliceCount-(arrived==null ? 0 : arrived.Length));
                if(devBackup==null && DevNeedsBackup(devWoundedOfficers,devTotalOfficers)) {
                    devBackup=new DevBackupRequest { Stamp=stamp };
                    DevCasualties.Backup=devBackup;
                }
                if(devBackup!=null && (!devBackup.Complete || (devBackup.TotalCars>0 && state.DevSupportStamp!=stamp))) pending=Math.Max(1,pending);
                devBattleOutcome=DevCombatRules.Outcome(resisting,alive,pending);
                if(devBattleOutcome!=0) { DevCombatResult.Resolving=true; devEndAt=devBattleClock+8; }
            }
            if(devBattleOutcome>0 && remaining==0) DevPublishCombat(true);
            if(devBattleOutcome<0) {
                DevMakeBarricade(road);
                if(devBattleClock>=devEndAt && devSuppressing) DevPublishCombat(false);
            }
            DevRiotSuppression.Status="[DEV 진압] 경찰 "+alive+"명 · 저항 "+resisting+"명 · 대피 "+fleeing+"명";
        }
        private int DevNearest(DevCombatant[] group,Vector3 point,bool riot)
        {
            int nearest=-1; float best=float.MaxValue;
            for(int i=0;i<group.Length;i++) {
                DevCombatant member=group[i];
                if(member==null || member.Dead || member.Gone || member.Fleeing || member.Detained || (riot ? devRiotBorrowed[i] || DevCombatRules.Down(devBattleClock,member.DownUntil) : member.Hits>=member.Limit)) continue;
                // Cover officers must not shoot or gas the target an arrest team is approaching.
                if(riot && devArrestTargets!=null && devArrestTargets[i]) continue;
                float distance=(member.Position-point).sqrMagnitude;
                if(distance<best) { best=distance; nearest=i; }
            }
            return nearest;
        }
        private static Vector3 DevCombatMove(DevRoadRibbon road,Vector3 from,Vector3 to,float meters)
        {
            float station,side,goal,goalSide; Vector3 heading;
            road.Project(from,out station,out side); road.Project(to,out goal,out goalSide);
            return road.At(Mathf.MoveTowards(station,goal,meters),Mathf.MoveTowards(side,goalSide,meters),out heading);
        }
        private void DevStartFlee(DevCombatant person,UnrestView state,int index)
        {
            if(person.Dead) return;
            person.Fleeing=true; person.FleeAt=devBattleClock; person.ExitRoute=null; person.ExitApproach=false;
            Vector3[] doors=state.DevEscapeDoors;
            if(doors!=null && doors.Length>0) person.Exit=doors[devBattleRandom.Next(doors.Length)];
            else { Vector3 forward; person.Exit=state.DevRoad.At(index%2==0 ? 0 : state.DevRoad.Limit,0,out forward); }
        }
        private void DevFlee(DevCombatant person,DevRoadRibbon road,float meters)
        {
            if(DevCombatRules.Down(devBattleClock,person.DownUntil)) return;
            float station,side; Vector3 forward;
            road.Project(person.Position,out station,out side);
            if(person.ExitRoute!=road.Points || person.ExitLimit!=road.Limit) {
                road.Project(person.Exit,out person.ExitStation,out person.ExitSide);
                if(person.ExitStation>road.Limit) {
                    // The selected door is now beyond a broken road. Use a reachable route exit.
                    person.Exit=road.At(Math.Abs(station)<Math.Abs(station-road.Limit) ? 0 : road.Limit,0,out forward);
                    road.Project(person.Exit,out person.ExitStation,out person.ExitSide);
                }
                person.ExitRoute=road.Points; person.ExitLimit=road.Limit; person.ExitApproach=false;
            }
            // Commit to the short doorway approach; projecting it back onto a corner can otherwise loop.
            Vector3 before=person.Position;
            if(Math.Abs(person.ExitStation-station)<=1) person.ExitApproach=true;
            if(!person.ExitApproach) person.Position=road.At(Mathf.MoveTowards(station,person.ExitStation,meters),Mathf.MoveTowards(side,person.ExitSide,meters),out forward);
            else person.Position=Vector3.MoveTowards(person.Position,person.Exit,meters);
            person.Travel=person.Position-before; person.Moving=person.Travel.sqrMagnitude>.000001f;
            if((person.Position-person.Exit).sqrMagnitude<.4f) person.Gone=true;
        }
        private void DevPublishCombat(bool policeWon)
        { DevCombatResult.Current=new DevCombatResult { Stamp=devRiotStamp,Lead=devBattleLead,PoliceWon=policeWon }; }
        private static Vector3 DevVisualOffset(DevCombatant person,DevRoadRibbon road,Quaternion facing,DevCombatMotion pose)
        {
            if(road==null || (pose.Forward==0 && pose.Side==0)) return person.Position;
            DevLocate(person,road); Vector3 forward;
            road.At(person.Station,person.Side,out forward);
            Vector3 offset=facing*new Vector3(pose.Side,0,pose.Forward);
            return road.At(person.Station+Vector3.Dot(offset,forward),person.Side+Vector3.Dot(offset,new Vector3(forward.z,0,-forward.x)),out forward);
        }
        private bool DevSuppressionPolice(float delta)
        {
            if(!devSuppressing) return false;
            DevShowLines(CivicUnrest.View.DevRoad);
            for(int i=0;i<officers.Count;i++) {
                DevCombatant person=devPolice[i]; CrowdActor actor=officers[i];
                if(person!=null) DevProjectile(i%25,person,devBattleClock-person.ActionAt);
                bool visible=person!=null && !person.Dead && !person.Gone && CrowdActor.InView(person.Position);
                actor.Show(visible); if(!visible) continue;
                Vector3 toward=DevVisualMotion.LookDirection(person.Travel,person.Moving,person.Aim-person.Position);
                Quaternion facing=toward.sqrMagnitude>.01f ? Quaternion.LookRotation(toward) : Quaternion.identity;
                bool retreat=!person.Dead && devBattleOutcome<0 && devBattleClock>=devEndAt-5;
                bool down=person.Hits>=person.Limit && !retreat;
                DevCombatMotion motion=DevCombatMotion.Sample(devBattleClock,i,retreat ? 0 : person.Action,person.ActionAt,person.ReactionAt,person.ReactionHit,down ? person.FallAt : -100,0,down,person.Moving);
                actor.Pose(DevVisualOffset(person,CivicUnrest.View.DevRoad,facing,motion),facing,false,!down && (person.Moving || (!retreat && (Math.Abs(motion.Side)>.05f || motion.Forward>.05f))),true,devBattleClock,delta);
                float action=devBattleClock-person.ActionAt;
                actor.CombatBody(facing,motion);
                bool guarding=!down && !retreat && (person.Prisoner<0 || DevPoliceLineRules.CustodyStrike(devBattleClock,person.ActionAt));
                if(guarding) actor.PoliceGuard(facing,motion,devBattleClock,person.Moving || Math.Abs(motion.Side)>.05f || motion.Forward>.05f);
                // Pose the body first, then place held props at the final hand positions.
                if(!down && motion.Brace<=.05f) actor.PoliceAction(facing,person.Action==1 ? DevVisualMotion.Strike(action,.7f) : 0,person.Action==4 && action<2,Mathf.Clamp01(action/2));
                else actor.HideBaton();
                actor.CombatWeapon(facing,!down && motion.Brace<=.05f && person.Action==2 && action<1,false,false,action);
                actor.SetLiveAmmo(person.Armed);
                if(person.Prisoner>=0 && !DevPoliceLineRules.CustodyStrike(devBattleClock,person.ActionAt)) { actor.HideBaton(); actor.CombatWeapon(facing,false,false,false,action); }
                if(down) { actor.HideBaton(); actor.CombatWeapon(facing,false,false,false,action); }
                else if(retreat) { actor.HideBaton(); actor.Cower(facing,.4f); }
                actor.CasualtyMark(person.Dead ? 2 : 0,person.Position);
            }
            return true;
        }
        private void DevSuppressionCrowd(int index,CrowdActor actor,Quaternion facing,float delta)
        {
            DevCombatant person=devRioters[index]; if(person==null) { actor.SetPlacardVisible(true); return; }
            if(person.Action==2) DevProjectile(5+index%20,person,devBattleClock-person.ActionAt);
            bool visible=!person.Dead && !person.Gone && CrowdActor.InView(person.Position);
            actor.Show(visible); if(!visible) return;
            if(!DevCombatControls(index)) return;
            Vector3 direction=DevVisualMotion.LookDirection(person.Travel,person.Moving,(person.Fleeing ? person.Exit : person.Aim)-person.Position);
            if(direction.sqrMagnitude>.01f) facing=Quaternion.LookRotation(direction);
            if(person.Detained) {
                actor.CasualtyMark(0,Vector3.zero);
                actor.Pose(person.Position,facing,true,person.Moving,false,devBattleClock,delta);
                actor.SetPlacardVisible(false); actor.CombatWeapon(facing,false,false,false,0);
                actor.CustodyPose(facing,person.BoardingAt<0 ? 0 : Mathf.Clamp01((devBattleClock-person.BoardingAt)/1.4f));
                return;
            }
            bool down=DevCombatRules.Down(devBattleClock,person.DownUntil);
            DevCombatMotion motion=DevCombatMotion.Sample(devBattleClock,index,person.Fleeing ? 0 : person.Action,person.ActionAt,person.ReactionAt,person.ReactionHit,person.FallAt,person.DownUntil,person.Dead,person.Moving);
            actor.Pose(DevVisualOffset(person,CivicUnrest.View.DevRoad,facing,motion),facing,true,!down && (person.Fleeing || person.Moving || Math.Abs(motion.Side)>.05f || motion.Forward>.05f),false,person.Dead ? person.FallAt : devBattleClock,person.Dead ? 0 : delta);
            actor.SetPlacardVisible(false);
            float action=devBattleClock-person.ActionAt;
            actor.CombatBody(facing,motion);
            if(down || person.Dead) actor.CasualtyPose(facing,person.Dead,motion.Fall,devBattleClock);
            actor.CasualtyMark(person.Dead ? 2 : down ? 1 : 0,person.Position);
            bool gun=person.Weapon!=null && !down && !person.Fleeing;
            bool plank=!gun && !down && !person.Fleeing && motion.Brace<=.05f &&
                ((DevPoliceLineRules.Melee(index) && devBattleOutcome==0 && person.Action!=3) || (person.Action==1 && action<1));
            actor.CombatWeapon(facing,gun,plank,!gun && !down && !person.Fleeing && motion.Brace<=.05f && person.Action==2 && action<1,
                (gun && person.Action!=5) || (plank && person.Action!=1) ? 2 : action);
            if(gun) actor.SetLiveAmmo(person.Weapon.Live);
            if(person.Pickup!=null && person.Pickup.PickupTime>0) actor.MedicalPose(facing,person.Pickup.PickupTime);
            if(!down && person.Action==3 && action<1) actor.Cower(facing,1);
        }
        private GameObject DevProp(string name,Vector3 scale,Color color,PrimitiveType type)
        {
            Shader shader=Shader.Find("Diffuse"); if(shader==null) return null;
            GameObject obj=GameObject.CreatePrimitive(type); obj.name=name; obj.transform.parent=transform; obj.transform.localScale=scale;
            Collider collider=obj.GetComponent<Collider>(); if(collider!=null) { collider.enabled=false; Destroy(collider); }
            Material material=new Material(shader); material.color=color; devBattleMaterials.Add(material); obj.GetComponent<Renderer>().sharedMaterial=material;
            return obj;
        }
        private void DevProjectile(int slot,DevCombatant person,float age)
        {
            bool show=person.Action==2 && age>=0 && age<.55f && person.Hits<person.Limit;
            if(devProjectiles[slot]==null && show) devProjectiles[slot]=DevProp("CivicPressure_Projectile",Vector3.one*.12f,new Color(.3f,.3f,.28f),PrimitiveType.Sphere);
            GameObject obj=devProjectiles[slot]; if(obj==null) return; obj.SetActive(show);
            if(show) { float t=age/.55f; obj.transform.position=Vector3.Lerp(person.Position+Vector3.up*1.3f,person.Aim+Vector3.up,t)+Vector3.up*((float)Math.Sin(t*Math.PI)*1.5f); }
        }
        private void DevMakeBarricade(DevRoadRibbon road)
        {
            if(devBarricades.Count>0) return;
            DevAnchorBarricade(road);
            // Existing wrecks remain where they stopped; only loose wooden barriers are added.
            for(int i=0;i<5;i++) {
                GameObject item=DevProp("CivicPressure_Barricade",new Vector3(1.6f,.18f,.2f),new Color(.32f,.21f,.12f),PrimitiveType.Cube);
                if(item==null) break;
                Vector3 forward; item.transform.position=road.At(devBarricadeStation,(i-2)*1.5f,out forward)+Vector3.up*(.35f+i%2*.3f);
                item.transform.rotation=Quaternion.LookRotation(forward)*Quaternion.Euler(0,i*13,25-i*12); devBarricades.Add(item);
            }
        }
    }
}
