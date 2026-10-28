using System;
using System.Collections.Generic;
using UnityEngine;

namespace CivicPressure
{
    // One object follows a weapon through every owner; deaths never duplicate it.
    internal sealed class DevLootWeapon
    {
        internal DevCombatant Owner, Candidate;
        internal bool Live;
        internal float PickupTime;
        internal bool Available { get { return Owner!=null && Owner.Dead; } }
        internal static bool Eligible(DevCombatant person,float clock)
        { return person!=null && !person.Dead && !person.Gone && !person.Fleeing && !person.Detained && person.Weapon==null && !DevCombatRules.Down(clock,person.DownUntil); }
        internal bool Reserve(DevCombatant person,float clock)
        {
            if(!Available || Candidate!=null || !Eligible(person,clock) || person.Pickup!=null) return false;
            Candidate=person; person.Pickup=this; PickupTime=0; return true;
        }
        internal void Release()
        {
            if(Candidate!=null && Candidate.Pickup==this) Candidate.Pickup=null;
            Candidate=null; PickupTime=0;
        }
        internal bool Take(DevCombatant person,float clock)
        {
            if(!Available || Candidate!=person || !Eligible(person,clock)) return false;
            Owner.Weapon=null; Owner=person; person.Weapon=this; Release(); return true;
        }
    }
    internal sealed class DevDeath
    {
        internal DevCombatant Person;
        internal CitizenInfo Skin;
        internal bool Police, Settled, WeaponShown;
        internal Quaternion Facing;
        internal CrowdActor Actor;
    }
    public sealed partial class CrowdPreview
    {
        private readonly List<DevDeath> devDeaths=new List<DevDeath>();
        private readonly List<DevLootWeapon> devLoot=new List<DevLootWeapon>();
        private float devNextLoot;
        private void DevRecordDeath(DevCombatant person,bool police,int index)
        {
            person.Casualty.Record(police,true);
            if(person.DeathRecorded) return;
            person.DeathRecorded=true; person.Moving=false; person.Action=0;
            if(person.Pickup!=null) person.Pickup.Release();
            if(police) {
                person.Weapon=new DevLootWeapon { Owner=person,Live=person.Armed };
                devLoot.Add(person.Weapon);
            }
            List<CitizenInfo> pool=police ? policeSkins : skins;
            Vector3 facing=person.Aim-person.Position; facing.y=0;
            devDeaths.Add(new DevDeath { Person=person,Police=police,Skin=pool.Count==0 ? null : pool[index%pool.Count],
                Facing=facing.sqrMagnitude>.01f ? Quaternion.LookRotation(facing) : Quaternion.identity });
        }
        private void DevResetDeaths()
        {
            foreach(DevDeath body in devDeaths) if(body.Actor!=null) body.Actor.Dispose();
            foreach(DevLootWeapon weapon in devLoot) weapon.Release();
            devDeaths.Clear(); devLoot.Clear(); devNextLoot=0;
        }
        private void DevDrawDeaths()
        {
            int budget=1;
            foreach(DevDeath body in devDeaths) {
                DevCombatant person=body.Person;
                bool visible=CrowdActor.InView(person.Position);
                if(body.Actor==null && visible && budget>0) {
                    body.Actor=new CrowdActor(transform,body.Skin,0,body.Police); budget--;
                }
                if(body.Actor==null) continue;
                body.Actor.Show(visible); if(!visible) continue;
                bool weapon=person.Weapon!=null && person.Weapon.Available;
                if(!body.Settled || body.WeaponShown!=weapon) {
                    body.Actor.Pose(person.Position,body.Facing,false,false,body.Police,person.FallAt,0);
                    body.Actor.SetPlacardVisible(false); body.Actor.HideBaton();
                    float fall=Mathf.SmoothStep(0,1,Mathf.Clamp01((devBattleClock-person.FallAt)/.4f));
                    body.Actor.CasualtyPose(body.Facing,true,fall,person.FallAt);
                    body.Actor.GroundWeapon(body.Facing,person.Position,weapon,weapon && person.Weapon.Live);
                    body.Settled=fall>=1; body.WeaponShown=weapon;
                }
                body.Actor.CasualtyMark(2,person.Position);
            }
        }
        private void DevPlanLoot(DevRoadRibbon road)
        {
            if(devBattleClock<devNextLoot) return;
            devNextLoot=devBattleClock+.25f;
            foreach(DevLootWeapon weapon in devLoot) {
                if(weapon.Candidate!=null && (!weapon.Available || !DevLootWeapon.Eligible(weapon.Candidate,devBattleClock))) weapon.Release();
                if(!weapon.Available || weapon.Candidate!=null) continue;
                // Only reachable bodies on the current road ribbon; no shortcuts through buildings.
                float station,side; Vector3 forward;
                road.Project(weapon.Owner.Position,out station,out side);
                if((road.At(station,side,out forward)-weapon.Owner.Position).sqrMagnitude>2.25f) continue;
                DevCombatant closest=null; float best=12*12;
                for(int i=0;i<devRioters.Length;i++) {
                    DevCombatant person=devRioters[i];
                    if(devRiotBorrowed[i] || !DevLootWeapon.Eligible(person,devBattleClock) || person.Pickup!=null) continue;
                    float distance=(person.Position-weapon.Owner.Position).sqrMagnitude;
                    if(distance<best) { best=distance; closest=person; }
                }
                if(closest!=null) weapon.Reserve(closest,devBattleClock);
            }
        }
        private bool DevPickupWeapon(DevCombatant person,DevRoadRibbon road,float delta)
        {
            DevLootWeapon weapon=person.Pickup; if(weapon==null) return false;
            if(!weapon.Available || !DevLootWeapon.Eligible(person,devBattleClock) || devBattleOutcome>0) { weapon.Release(); return false; }
            float station,side; Vector3 forward;
            road.Project(weapon.Owner.Position,out station,out side);
            Vector3 goal=road.At(station,side,out forward);
            if((goal-weapon.Owner.Position).sqrMagnitude>2.25f) { weapon.Release(); return false; }
            person.Aim=weapon.Owner.Position; person.Action=0;
            DevMoveSlot(person,road,station,side,delta*2.1f);
            if((person.Position-goal).sqrMagnitude>1.2f*1.2f) { weapon.PickupTime=0; return true; }
            person.Moving=false; weapon.PickupTime+=delta;
            if(weapon.PickupTime>=1 && weapon.Take(person,devBattleClock)) person.NextAttack=devBattleClock+.5f;
            return true;
        }
        private void DevHitOfficer(DevCombatant officer,int index,bool gun,bool liveShot)
        {
            officer.Casualty.Record(true,false);
            if(officer.MarkWounded()) devWoundedOfficers++;
            bool killed=liveShot && DevCombatRules.LiveRound(officer,devBattleClock,devBattleRandom.NextDouble());
            officer.Hits=killed ? officer.Limit : officer.Hits+1;
            if(officer.Hits<officer.Limit || officer.Treatment!=null) return;
            officer.FallAt=devBattleClock;
            if(!gun) officer.Dead=devBattleRandom.NextDouble()<.1;
            // Rubber rounds can injure, but do not use the old melee fatality roll.
            officer.Treatment=DevCasualties.Request(CivicUnrest.DevRiotStamp,officer.Position,officer.Dead);
            if(officer.Dead) DevRecordDeath(officer,true,index);
            devNextDecision=devBattleClock+1;
        }
    }
}
