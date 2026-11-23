using System;
using System.Collections.Generic;
using ColossalFramework;

namespace CivicPressure
{
    internal sealed class CityCensus
    {
        internal static readonly ServiceWait CrimeWait = new ServiceWait(), MedicalWait = new ServiceWait();
        internal static readonly ServiceWait EducationWait = new ServiceWait(), HousingWait = new ServiceWait();
        internal static readonly ServiceWait DeathcareWait = new ServiceWait(), FireWait = new ServiceWait();
        private readonly bool[] schoolUnlocked = new bool[3];
        internal static void ForgetCitizen(uint id)
        { MedicalWait.Forget(id); EducationWait.Forget(id); HousingWait.Forget(id); }
        private struct Sample
        {
            internal byte District;
            internal int Harms, Severe, Needs, Pending;
            internal ServiceComplaintSite Deathcare, Fire, FireCoverage, MedicalCoverage, PoliceCoverage;
        }
        internal sealed class ServiceImpact
        {
            internal ServiceComplaintSite Site;
            internal int Residents;
            internal int ResidenceDistrict = -1;
        }
        private readonly Sample[] samples;
        internal readonly int[] Residents = new int[256], Buildings = new int[256], Abandoned = new int[256], Warning = new int[256], Major = new int[256];
        internal readonly int[][] Affected = new int[256][], Severe = new int[256][];
        internal int Created, NoHome;
        internal readonly int[] PendingByDistrict = new int[256];
        internal readonly int[] Housed = new int[256];
        internal int Unlocated;
        internal readonly int[] UnlocatedAffected = new int[26], UnlocatedSevere = new int[26];
        internal readonly Dictionary<int, RoadResidents> Roads = new Dictionary<int, RoadResidents>();
        internal readonly HotspotCensus Hotspots=new HotspotCensus();
        internal readonly Dictionary<int,ComplaintHome> Homes=new Dictionary<int,ComplaintHome>();
        internal readonly Dictionary<int,ComplaintSite> Sites=new Dictionary<int,ComplaintSite>();
        internal readonly Dictionary<int, Dictionary<ushort, ServiceImpact>> ServiceSites =
            new Dictionary<int, Dictionary<ushort, ServiceImpact>>();
        private void RememberService(int residence, int reason, ServiceComplaintSite site)
        {
            if (site == null) return;
            int key = site.District * ComplaintRules.Slots + reason;
            Dictionary<ushort, ServiceImpact> buildings;
            if (!ServiceSites.TryGetValue(key, out buildings))
            {
                buildings = new Dictionary<ushort, ServiceImpact>();
                ServiceSites.Add(key, buildings);
            }
            ServiceImpact impact;
            if (!buildings.TryGetValue(site.Building, out impact))
            {
                impact = new ServiceImpact { Site = site };
                buildings.Add(site.Building, impact);
            }
            else impact.Site = Prefer(impact.Site, site);
            if (residence >= 0) {
                impact.Residents++;
                if (impact.ResidenceDistrict < 0 || impact.ResidenceDistrict == site.District)
                    impact.ResidenceDistrict = residence;
            }
        }

        private static ServiceComplaintSite Prefer(ServiceComplaintSite current, ServiceComplaintSite candidate)
        {
            if (current == null) return candidate;
            if (candidate == null) return current;
            if (candidate.Severity > current.Severity ||
                (candidate.Severity == current.Severity && candidate.Cause == ServiceComplaintCause.Fire &&
                    current.Cause != ServiceComplaintCause.Fire)) return candidate;
            return current;
        }

        private static void MergeSample(ref Sample sample, Sample source)
        {
            sample.Harms |= source.Harms;
            sample.Severe |= source.Severe;
            sample.Pending |= source.Pending;
            sample.Deathcare = Prefer(sample.Deathcare, source.Deathcare);
            sample.Fire = Prefer(sample.Fire, source.Fire);
        }
        private void Remember(int region,int reason,uint citizen,ushort home,Building[] buildings)
        {
            if(region<0 || home==0 || home>=buildings.Length || !Suitable(buildings[home]))return;
            int key=region*ComplaintRules.Slots+reason;
            if(!Homes.ContainsKey(key))Homes.Add(key,new ComplaintHome{Citizen=citizen,Building=home,BuildIndex=buildings[home].m_buildIndex});
        }
        private void RememberSite(int region,int reason,ushort work,ushort visit,Building[] buildings)
        {
            int key=region*ComplaintRules.Slots+reason;
            if(Sites.ContainsKey(key))return;
            ushort building=work!=0 && work<samples.Length && (samples[work].Harms&(1<<reason))!=0 ? work :
                visit!=0 && visit<samples.Length && (samples[visit].Harms&(1<<reason))!=0 ? visit : (ushort)0;
            if(building!=0 && (ComplaintMapping.Harms(buildings[building].m_problems)&(1<<reason))==0) {
                ushort parent=buildings[building].m_parentBuilding;
                building=parent!=0 && parent<samples.Length && (ComplaintMapping.Harms(buildings[parent].m_problems)&(1<<reason))!=0 ? parent : (ushort)0;
            }
            if(building==0 || !Suitable(buildings[building]))return;
            Sites.Add(key,new ComplaintSite{Building=building,BuildIndex=buildings[building].m_buildIndex,Reason=reason});
        }
        private readonly AmenityCoverage amenities;
        private int buildingCursor = 1, citizenCursor = 1, citizenCapacity;
        internal int Progress { get { return (int)(100L * (buildingCursor + citizenCursor - 2) / Math.Max(1, samples.Length + citizenCapacity - 2)); } }

        internal CityCensus(int capacity)
        {
            samples = new Sample[capacity];
            UnlockManager unlocks = Singleton<UnlockManager>.instance;
            amenities = new AmenityCoverage();
            if (unlocks.Unlocked(ItemClass.Service.Education))
                for (uint p = 0; p < PrefabCollection<BuildingInfo>.LoadedCount(); p++) {
                    BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded(p);
                    if (info == null || !(info.m_buildingAI is SchoolAI) || info.m_class == null) continue;
                    int level = (int)info.m_class.m_level;
                    if (level >= 0 && level < 3 && Singleton<UnlockManager>.instance.Unlocked(info.m_UnlockMilestone)) schoolUnlocked[level] = true;
                }
            citizenCapacity = Singleton<CitizenManager>.instance.m_citizens.m_buffer.Length;
            for (int i = 0; i < 256; i++) { Affected[i] = new int[Happiness.Weights.Length]; Severe[i] = new int[Happiness.Weights.Length]; }
        }

        internal bool Step()
        {
            if (!amenities.Step()) return false;
            long budgetEnd=System.Diagnostics.Stopwatch.GetTimestamp()+System.Diagnostics.Stopwatch.Frequency/500;
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            if (buildingCursor < samples.Length)
            {
                int end = Math.Min(buildingCursor + 4096, samples.Length);
                for (; buildingCursor < end; buildingCursor++)
                {
                    if((buildingCursor&63)==0 && System.Diagnostics.Stopwatch.GetTimestamp()>=budgetEnd) break;
                    Building b = buildings[buildingCursor];
                    if (!CivicSimulation.Live(b))
                    {
                        CrimeWait.Forget((uint)buildingCursor);
                        DeathcareWait.Forget((uint)buildingCursor);
                        FireWait.Forget((uint)buildingCursor);
                        continue;
                    }
                    byte district = Singleton<DistrictManager>.instance.GetDistrict(b.m_position);
                    Sample sample = Read((ushort)buildingCursor, b); sample.District = district;
                    RememberService(-1, 13, sample.Deathcare);
                    RememberService(-1, 14, sample.Fire);
                    if (b.Info != null && b.Info.m_class.m_service == ItemClass.Service.Residential)
                    {
                        sample.Needs |= amenities.Missing(b.m_position);
                        ushort id = (ushort)buildingCursor;
                        sample.FireCoverage = CoverageGap(id, b, ServiceComplaintCause.FireCoverage);
                        sample.PoliceCoverage = CoverageGap(id, b, ServiceComplaintCause.PoliceCoverage);
                        sample.MedicalCoverage = CoverageGap(id, b, ServiceComplaintCause.MedicalCoverage);
                        if (sample.FireCoverage != null) sample.Needs |= 1 << 14;
                        if (sample.PoliceCoverage != null) sample.Needs |= 1 << 12;
                        if (sample.MedicalCoverage != null) sample.Needs |= 1 << 11;
                    }
                    if (b.m_parentBuilding != 0 && b.m_parentBuilding < buildings.Length && CivicSimulation.Live(buildings[b.m_parentBuilding]))
                    {
                        Sample parent = Read(b.m_parentBuilding, buildings[b.m_parentBuilding]);
                        MergeSample(ref sample, parent);
                    }
                    samples[buildingCursor] = sample;
                    if (b.m_parentBuilding != 0) continue;
                    bool abandoned = (b.m_flags & Building.Flags.Abandoned) != 0;
                    if (abandoned) Abandoned[district]++;
                    if (b.Info != null)
                    {
                        ItemClass.Service service = b.Info.m_class.m_service;
                        if (service == ItemClass.Service.Residential || service == ItemClass.Service.Commercial ||
                            service == ItemClass.Service.Industrial || service == ItemClass.Service.Office || abandoned) Buildings[district]++;
                    }
                    else if (abandoned) Buildings[district]++;
                    if (sample.Harms != 0) { if (sample.Severe != 0) Major[district]++; else Warning[district]++; }
                }
                return false;
            }
            Citizen[] citizens = Singleton<CitizenManager>.instance.m_citizens.m_buffer;
            int last = Math.Min(citizenCursor + 16384, citizens.Length);
            for (; citizenCursor < last; citizenCursor++)
            {
                if((citizenCursor&63)==0 && System.Diagnostics.Stopwatch.GetTimestamp()>=budgetEnd) break;
                Citizen c = citizens[citizenCursor];
                if ((c.m_flags & Citizen.Flags.Created) == 0) { ForgetCitizen((uint)citizenCursor); continue; }
                Created++;
                if ((c.m_flags & (Citizen.Flags.Dead | Citizen.Flags.Tourist | Citizen.Flags.DummyTraffic | Citizen.Flags.MovingIn)) != 0) { ForgetCitizen((uint)citizenCursor); continue; }
                ushort home = c.m_homeBuilding;
                bool hasHome = home != 0 && home < samples.Length && CivicSimulation.Live(buildings[home]);
                if (!hasHome) NoHome++;
                Sample sample = hasHome ? samples[home] : new Sample();
                // A workplace is not proof of residence. Unknown residence must not mean district zero.
                int district = ComplaintMapping.ResidenceDistrict(hasHome, hasHome ? Singleton<DistrictManager>.instance.GetDistrict(buildings[home].m_position) : 0);
                sample.Harms |= sample.Needs;
                bool housing = hasHome && Suitable(buildings[home]);
                if (housing) Housed[district]++;
                bool placement = c.m_workBuilding != 0 && c.m_workBuilding < buildings.Length && Suitable(buildings[c.m_workBuilding]);
                int needs = ComplaintMapping.EssentialHarms(c, housing, placement);
                long observedAt = Singleton<SimulationManager>.instance.m_currentGameTime.Ticks;
                Citizen.AgeGroup schoolAge = Citizen.GetAgeGroup(c.Age);
                int schoolLevel = schoolAge == Citizen.AgeGroup.Child ? 0 : schoolAge == Citizen.AgeGroup.Teen ? 1 : c.Education2 ? 2 : c.Education1 ? 1 : 0;
                bool assignedSchool = placement && buildings[c.m_workBuilding].Info != null &&
                    buildings[c.m_workBuilding].Info.m_class != null &&
                    buildings[c.m_workBuilding].Info.m_class.m_service == ItemClass.Service.Education;
                bool schoolNeeded = (ComplaintMapping.EssentialHarms(c, housing, assignedSchool) & (1 << 10)) != 0;
                needs &= ~((1 << 8) | (1 << 10));
                if (EducationWait.Observe((uint)citizenCursor, (uint)(schoolLevel + 1), schoolNeeded && schoolUnlocked[schoolLevel], observedAt)) needs |= 1 << 10;
                if (HousingWait.Observe((uint)citizenCursor, 1, !housing, observedAt)) needs |= 1 << 8;
                sample.Harms |= needs;
                if ((needs & (1 << 8)) != 0) sample.Severe |= 1 << 8;
                Merge(ref sample, c.m_workBuilding, buildings);
                if (c.CurrentLocation == Citizen.Location.Visit) Merge(ref sample, c.m_visitBuilding, buildings);
                RememberService(district, 13, sample.Deathcare);
                RememberService(district, 14, sample.Fire);
                if (sample.FireCoverage != null && (sample.Fire == null || sample.Fire.Building != sample.FireCoverage.Building))
                    RememberService(district, 14, sample.FireCoverage);
                else if (sample.FireCoverage != null)
                    RememberService(-1, 14, sample.FireCoverage);
                RememberService(district, 11, sample.MedicalCoverage);
                RememberService(district, 12, sample.PoliceCoverage);
                // Utility shortage counters on Citizen retain history until ResidentAI next updates.
                // Current home/work/visit building notifications above are the source for active outages.
                int health = ComplaintMapping.HealthSeverity(c, sample.Severe);
                bool receivingCare = c.CurrentLocation == Citizen.Location.Visit && c.m_visitBuilding != 0 &&
                    c.m_visitBuilding < buildings.Length && Suitable(buildings[c.m_visitBuilding]) &&
                    buildings[c.m_visitBuilding].Info != null && buildings[c.m_visitBuilding].Info.m_buildingAI is HospitalAI;
                bool awaitingCare = Singleton<UnlockManager>.instance.Unlocked(ItemClass.Service.HealthCare) && health > 0 && !receivingCare;
                if (MedicalWait.Observe((uint)citizenCursor, c.m_homeBuilding, awaitingCare, observedAt))
                {
                    AddState(ref sample, 6, health);
                }
                else if (awaitingCare) sample.Pending |= 1 << 6;
                if (district >= 0) PendingByDistrict[district] |= sample.Pending;
                CitizenInstance[] instances = Singleton<CitizenManager>.instance.m_instances.m_buffer;
                if (c.m_instance != 0 && c.m_instance < instances.Length) {
                    CitizenInstance waiting = instances[c.m_instance];
                    int transit = ComplaintMapping.TransitSeverity(waiting.m_flags, waiting.m_waitCounter);
                    int owner=Hotspots.Waiting(district,waiting,transit); Remember(owner,17,(uint)citizenCursor,home,buildings);
                    if (transit > 0) sample.Harms |= 1 << 17;
                    if (transit > 1) sample.Severe |= 1 << 17;
                }
                if((sample.Harms&(1<<2))!=0) { int owner=Hotspots.Traffic(district,home,c.m_workBuilding,c.CurrentLocation==Citizen.Location.Visit ? c.m_visitBuilding : (ushort)0,buildings); Remember(owner,2,(uint)citizenCursor,home,buildings); }
                if (district < 0) {
                    Unlocated++;
                    for (int reason = 0; reason < Happiness.Weights.Length; reason++) {
                        if ((sample.Harms & (1 << reason)) != 0) UnlocatedAffected[reason]++;
                        if ((sample.Severe & (1 << reason)) != 0) UnlocatedSevere[reason]++;
                    }
                    continue;
                }
                Residents[district]++;
                RoadResidents roadSample = null;
                ushort access = hasHome ? buildings[home].m_accessSegment : (ushort)0;
                NetSegment[] segments = Singleton<NetManager>.instance.m_segments.m_buffer;
                if (access != 0 && access < segments.Length && (segments[access].m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) == NetSegment.Flags.Created)
                {
                    int key = district * 65536 + access;
                    if (!Roads.TryGetValue(key, out roadSample))
                    { roadSample = new RoadResidents { Road = access, Region = (byte)district, BuildIndex = segments[access].m_buildIndex }; Roads.Add(key, roadSample); }
                    roadSample.Residents++;
                }
                for (int reason = 0; reason < Happiness.Weights.Length; reason++)
                {
                    if ((sample.Harms & (1 << reason)) != 0) {
                        Affected[district][reason]++;
                        if(reason!=2 && reason!=17)Remember(district,reason,(uint)citizenCursor,home,buildings);
                        if(reason>=19 && reason<=24)RememberSite(district,reason,c.m_workBuilding,c.CurrentLocation==Citizen.Location.Visit ? c.m_visitBuilding : (ushort)0,buildings);
                    }
                    if ((sample.Severe & (1 << reason)) != 0) Severe[district][reason]++;
                    if (roadSample != null)
                    {
                        if ((sample.Harms & (1 << reason)) != 0) roadSample.Affected[reason]++;
                        if ((sample.Severe & (1 << reason)) != 0) roadSample.Severe[reason]++;
                    }
                }
            }
            return citizenCursor >= citizens.Length;
        }

        private void Merge(ref Sample sample, ushort id, Building[] buildings)
        {
            if (id == 0 || id >= samples.Length || !CivicSimulation.Live(buildings[id])) return;
            MergeSample(ref sample, samples[id]);
        }

        private static bool Suitable(Building b)
        {
            return CivicSimulation.Live(b) && (b.m_flags & Building.Flags.Completed) != 0 &&
                (b.m_flags & (Building.Flags.Abandoned | Building.Flags.Collapsed | Building.Flags.Demolishing)) == 0;
        }

        private static Sample Read(ushort id, Building b)
        {
            int mask = ComplaintMapping.Harms(b.m_problems);
            int severe = b.m_problems.IsMajor || b.m_problems.IsFatal || (b.m_flags & Building.Flags.Abandoned) != 0 ? mask : 0;
            // Replace unrelated notification-group severity with each building overlay's own scalar.
            const int independent = (1 << 4) | (1 << 5) | (1 << 13);
            mask &= ~independent; severe &= ~independent;
            Sample result = new Sample { Harms = mask, Severe = severe };
            long now = Singleton<SimulationManager>.instance.m_currentGameTime.Ticks;
            bool awaitingCollection = AwaitingCollection(b);
            long deathWait = DeathcareWait.ObserveDuration(id, b.m_buildIndex, awaitingCollection, now);
            if (awaitingCollection)
            {
                int severity = BuildingServiceRules.DeathcareSeverity(deathWait);
                result.Deathcare = ServiceSite(id, b, ServiceComplaintCause.Deathcare, severity, deathWait);
                AddState(ref result, 13, severity);
            }
            bool burning = b.m_fireIntensity > 0;
            long fireWait = FireWait.ObserveDuration(id, b.m_buildIndex, burning, now);
            if (burning)
            {
                int severity = BuildingServiceRules.ActiveFireSeverity(fireWait);
                result.Fire = ServiceSite(id, b, ServiceComplaintCause.Fire, severity, fireWait);
                AddState(ref result, 14, severity);
            }
            if (Suitable(b) && b.Info != null && b.Info.m_buildingAI is CommonBuildingAI)
                AddState(ref result, 4, BuildingServiceRules.GarbageSeverity(b.m_garbageBuffer));
            int crime = Suitable(b) && b.Info != null && b.Info.m_buildingAI is CommonBuildingAI &&
                Singleton<UnlockManager>.instance.Unlocked(ItemClass.Service.PoliceDepartment)
                ? BuildingServiceRules.CrimeSeverity(b.m_crimeBuffer, b.m_citizenCount) : 0;
            if (CrimeWait.Observe(id, b.m_buildIndex, crime > 0, now))
                AddState(ref result, 5, crime);
            else if (crime > 0) result.Pending |= 1 << 5;
            // Congestion is a separate proxy; a building's red power icon must not amplify it.
            NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
            ushort road = b.m_accessSegment;
            if (road != 0 && road < roads.Length &&
                (roads[road].m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) == NetSegment.Flags.Created &&
                roads[road].Info != null && roads[road].Info.m_class.m_service == ItemClass.Service.Road && roads[road].m_trafficDensity >= 80) AddState(ref result, 2, roads[road].m_trafficDensity >= 95 ? 2 : 1);
            return result;
        }

        internal static bool AwaitingCollection(Building b)
        {
            return (b.m_problems.m_Problems1 & Notification.Problem1.Death) != 0;
        }

        private static ServiceComplaintSite CoverageGap(ushort id, Building building, ServiceComplaintCause cause)
        {
            return MissingCoverage(building, cause) ? ServiceSite(id, building, cause, 1, 0) : null;
        }

        internal static bool MissingCoverage(Building building, ServiceComplaintCause cause)
        {
            if (!Suitable(building) || building.Info == null || building.Info.m_class == null ||
                building.Info.m_class.m_service != ItemClass.Service.Residential) return false;
            UnlockManager unlocks = Singleton<UnlockManager>.instance;
            if (cause == ServiceComplaintCause.FireCoverage)
                return unlocks.Unlocked(ItemClass.Service.FireDepartment) &&
                    Singleton<CoverageManager>.instance.FindFireCoverage(building.CalculateAccessPosition(0f, 0f)) == 0;
            bool medical = cause == ServiceComplaintCause.MedicalCoverage;
            if (!unlocks.Unlocked(medical ? ItemClass.Service.HealthCare : ItemClass.Service.PoliceDepartment)) return false;
            int value;
            Singleton<ImmaterialResourceManager>.instance.CheckLocalResource(medical ?
                ImmaterialResourceManager.Resource.HealthCare : ImmaterialResourceManager.Resource.PoliceDepartment,
                building.m_position, out value);
            return value == 0;
        }

        private static ServiceComplaintSite ServiceSite(ushort id, Building b, ServiceComplaintCause cause, int severity, long elapsed)
        {
            return new ServiceComplaintSite { Building = id, BuildIndex = b.m_buildIndex,
                District = Singleton<DistrictManager>.instance.GetDistrict(b.m_position), Cause = cause,
                Severity = severity, ObservedHours = elapsed / (double)GameClock.HourTicks };
        }

        private static void AddState(ref Sample sample, int reason, int severity)
        {
            if (severity > 0) sample.Harms |= 1 << reason;
            if (severity > 1) sample.Severe |= 1 << reason;
        }
    }

    // Scalars used by CS1 CommonBuildingAI/ResidentialBuildingAI.GetColor, independent of the open overlay.
    // 60% toward the bad color is our complaint threshold, not a native game warning threshold.
    internal static class BuildingServiceRules
    {
        internal static int Severity(int bad, int full)
        {
            if (full <= 0 || bad <= 0) return 0;
            return (long)bad * 5 >= (long)full * 4 ? 2 : (long)bad * 5 >= (long)full * 3 ? 1 : 0;
        }
        internal static int HealthSeverity(int health, int residents)
        {
            return residents <= 0 ? 0 : Severity(5 - (int)Citizen.GetHealthLevel(health), 5);
        }
        internal static int CrimeSeverity(int crime, int residents)
        {
            return residents <= 0 ? 0 : Severity(crime, residents * 100);
        }
        internal static int GarbageSeverity(int garbage)
        {
            return Severity(Math.Min(100, garbage / 50), 100);
        }
        internal static int DeathcareSeverity(long observed)
        {
            if (observed >= GameClock.Hours(6)) return 2;
            return 1;
        }
        internal static int ActiveFireSeverity(long observed)
        {
            return observed >= GameClock.Hours(3) ? 2 : 0;
        }
        internal static int EducationSeverity(Citizen citizen, Building home)
        {
            Citizen.AgeGroup age = Citizen.GetAgeGroup(citizen.Age);
            int educated, population;
            if (age == Citizen.AgeGroup.Child && !citizen.Education1)
            { educated = home.m_education1; population = home.m_teens + home.m_youngs + home.m_adults + home.m_seniors; }
            else if (age == Citizen.AgeGroup.Teen && !citizen.Education2)
            { educated = home.m_education2; population = home.m_youngs + home.m_adults + home.m_seniors; }
            else if (!citizen.Education1)
            { educated = home.m_education1; population = home.m_teens + home.m_youngs + home.m_adults + home.m_seniors; }
            else if (!citizen.Education2)
            { educated = home.m_education2; population = home.m_youngs + home.m_adults + home.m_seniors; }
            else
            { educated = home.m_education3; population = home.m_youngs * 2 / 3 + home.m_adults + home.m_seniors; }
            int good = educated * 100;
            if (population != 0) good = (good + (population >> 1)) / population;
            good = Math.Min(100, good);
            return Severity(100 - good, 100);
        }
    }

    internal static class ComplaintMapping
    {
        internal static int ResidenceDistrict(bool homeKnown, int district) { return homeKnown ? district : -1; }
        internal static int TransitSeverity(CitizenInstance.Flags flags, byte wait)
        {
            CitizenInstance.Flags needed = CitizenInstance.Flags.Created | CitizenInstance.Flags.WaitingTransport;
            if ((flags & needed) != needed) return 0;
            // Vanilla resets the counter on BoredOfWaiting: preserve severe severity across that reset.
            if ((flags & CitizenInstance.Flags.BoredOfWaiting) != 0 || wait >= 192) return 2;
            return wait >= 64 ? 1 : 0;
        }
        internal static int EssentialHarms(Citizen citizen, bool housing, bool placement)
        {
            if ((citizen.m_flags & (Citizen.Flags.Dead | Citizen.Flags.Tourist | Citizen.Flags.DummyTraffic | Citizen.Flags.MovingIn)) != 0) return 0;
            int mask = housing ? 0 : 1 << 8;
            Citizen.AgeGroup age = Citizen.GetAgeGroup(citizen.Age);
            bool student = (citizen.m_flags & Citizen.Flags.Student) != 0;
            bool schoolRequired = (age == Citizen.AgeGroup.Child && !citizen.Education1) ||
                (age == Citizen.AgeGroup.Teen && !citizen.Education2) || student;
            if (schoolRequired && (!student || !placement)) mask |= 1 << 10;
            // Match Citizen's native unemployment statistics, not building readiness or RCI demand.
            if (!student && (age == Citizen.AgeGroup.Young || age == Citizen.AgeGroup.Adult) && citizen.Unemployed != 0) mask |= 1 << 9;
            return mask;
        }
        internal static int HealthSeverity(Citizen citizen, int severeHarms)
        {
            if ((citizen.m_flags & Citizen.Flags.Dead) != 0 ||
                (citizen.m_flags & (Citizen.Flags.Sick | Citizen.Flags.BadHealth)) == 0) return 0;
            // The vanilla health warning icons are DirtyWater/Pollution/Noise, not a separate Sick problem bit.
            return Citizen.GetHealthLevel(citizen.m_health) == Citizen.Health.VerySick ||
                (severeHarms & 3) != 0 ? 2 : 1;
        }
        internal static bool MedicalServiceHarm(bool coverageMissing, int healthSeverity)
        {
            return coverageMissing && healthSeverity > 0;
        }
        internal static bool ServiceCoverageHarm(bool serviceUnlocked, bool coverageMissing)
        {
            return serviceUnlocked && coverageMissing;
        }
        // CS1 stores the white/red background severity for the entire building notification group.
        internal static int Harms(Notification.ProblemStruct problems)
        {
            Notification.Problem1 p = problems.m_Problems1;
            int mask = 0;
            if ((p & (Notification.Problem1.Pollution | Notification.Problem1.DirtyWater)) != 0) mask |= 1;
            if ((p & Notification.Problem1.Noise) != 0) mask |= 1 << 1;
            if ((p & (Notification.Problem1.Electricity | Notification.Problem1.ElectricityNotConnected | Notification.Problem1.Water |
                Notification.Problem1.WaterNotConnected | Notification.Problem1.Sewage | Notification.Problem1.Heating | Notification.Problem1.HeatingNotConnected)) != 0) mask |= 1 << 3;
            if ((p & Notification.Problem1.Garbage) != 0) mask |= 1 << 4;
            if ((p & Notification.Problem1.Crime) != 0) mask |= 1 << 5;
            if ((p & Notification.Problem1.TaxesTooHigh) != 0) mask |= 1 << 7;
            if ((p & Notification.Problem1.Death) != 0) mask |= 1 << 13;
            if ((p & Notification.Problem1.RoadNotConnected) != 0) mask |= 1 << 18;
            if ((p & (Notification.Problem1.NoGoods | Notification.Problem1.NoFishingGoods)) != 0) mask |= 1 << 19;
            if ((p & (Notification.Problem1.NoResources | Notification.Problem1.NoFuel | Notification.Problem1.NoInputProducts | Notification.Problem1.NoNaturalResources)) != 0) mask |= 1 << 20;
            if ((p & (Notification.Problem1.NoPlaceforGoods | Notification.Problem1.NoPlaceForFishingGoods)) != 0) mask |= 1 << 21;
            // Qualified-worker shortages take priority so the same staffing alert is not charged twice.
            if ((p & Notification.Problem1.NoEducatedWorkers) != 0) mask |= 1 << 23;
            else if ((p & Notification.Problem1.NoWorkers) != 0) mask |= 1 << 22;
            if ((p & Notification.Problem1.NoCustomers) != 0) mask |= 1 << 24;
            if ((p & Notification.Problem1.Flood) != 0) mask |= 1 << 25;
            return mask;
        }
    }
}
