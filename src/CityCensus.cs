using System;
using System.Collections.Generic;
using ColossalFramework;

namespace CivicPressure
{
    internal sealed class CityCensus
    {
        private struct Sample { internal byte District; internal int Harms, Severe, Needs, SevereNeeds; }
        private readonly Sample[] samples;
        internal readonly int[] Residents = new int[256], Buildings = new int[256], Abandoned = new int[256], Warning = new int[256], Major = new int[256];
        internal readonly int[][] Affected = new int[256][], Severe = new int[256][];
        internal int Created, NoHome;
        internal readonly int[] Housed = new int[256];
        internal int Unlocated;
        internal readonly int[] UnlocatedAffected = new int[26], UnlocatedSevere = new int[26];
        internal readonly Dictionary<int, RoadResidents> Roads = new Dictionary<int, RoadResidents>();
        internal readonly HotspotCensus Hotspots=new HotspotCensus();
        internal readonly Dictionary<int,ComplaintHome> Homes=new Dictionary<int,ComplaintHome>();
        internal readonly Dictionary<int,ComplaintSite> Sites=new Dictionary<int,ComplaintSite>();
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
        private readonly AmenityCoverage amenities = new AmenityCoverage();
        private int buildingCursor = 1, citizenCursor = 1, citizenCapacity;
        internal int Progress { get { return (int)(100L * (buildingCursor + citizenCursor - 2) / Math.Max(1, samples.Length + citizenCapacity - 2)); } }

        internal CityCensus(int capacity)
        {
            samples = new Sample[capacity];
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
                    if (!CivicSimulation.Live(b)) continue;
                    byte district = Singleton<DistrictManager>.instance.GetDistrict(b.m_position);
                    Sample sample = Read((ushort)buildingCursor, b); sample.District = district;
                    if (b.Info != null && b.Info.m_class.m_service == ItemClass.Service.Residential)
                    {
                        sample.Needs |= amenities.Missing(b.m_position);
                        int local, total;
                        ImmaterialResourceManager resources = Singleton<ImmaterialResourceManager>.instance;
                        resources.CheckResource(ImmaterialResourceManager.Resource.HealthCare, b.m_position, out local, out total);
                        if (ComplaintMapping.ServiceCoverageHarm(Singleton<UnlockManager>.instance.Unlocked(ItemClass.Service.HealthCare), local <= 0) && (sample.Harms & (1 << 6)) != 0) { sample.Needs |= 1 << 11; if ((sample.Severe & (1 << 6)) != 0) sample.SevereNeeds |= 1 << 11; }
                        resources.CheckResource(ImmaterialResourceManager.Resource.PoliceDepartment, b.m_position, out local, out total);
                        if (ComplaintMapping.ServiceCoverageHarm(Singleton<UnlockManager>.instance.Unlocked(ItemClass.Service.PoliceDepartment), local <= 0) && (sample.Harms & (1 << 5)) != 0) { sample.Needs |= 1 << 12; if ((sample.Severe & (1 << 5)) != 0) sample.SevereNeeds |= 1 << 12; }
                    }
                    if (b.m_parentBuilding != 0 && b.m_parentBuilding < buildings.Length && CivicSimulation.Live(buildings[b.m_parentBuilding]))
                    {
                        Sample parent = Read(b.m_parentBuilding, buildings[b.m_parentBuilding]);
                        sample.Harms |= parent.Harms; sample.Severe |= parent.Severe;
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
                if ((c.m_flags & Citizen.Flags.Created) == 0) continue;
                Created++;
                if ((c.m_flags & (Citizen.Flags.Dead | Citizen.Flags.Tourist | Citizen.Flags.DummyTraffic | Citizen.Flags.MovingIn)) != 0) continue;
                ushort home = c.m_homeBuilding;
                bool hasHome = home != 0 && home < samples.Length && CivicSimulation.Live(buildings[home]);
                if (!hasHome) NoHome++;
                Sample sample = hasHome ? samples[home] : new Sample();
                // A workplace is not proof of residence. Unknown residence must not mean district zero.
                int district = ComplaintMapping.ResidenceDistrict(hasHome, hasHome ? Singleton<DistrictManager>.instance.GetDistrict(buildings[home].m_position) : 0);
                sample.Harms |= sample.Needs; sample.Severe |= sample.SevereNeeds;
                bool housing = hasHome && Suitable(buildings[home]);
                if (housing) Housed[district]++;
                bool placement = c.m_workBuilding != 0 && c.m_workBuilding < buildings.Length && Suitable(buildings[c.m_workBuilding]);
                int needs = ComplaintMapping.EssentialHarms(c, housing, placement);
                if ((needs & (1 << 10)) != 0)
                {
                    int education = housing && Singleton<UnlockManager>.instance.Unlocked(ItemClass.Service.Education) ?
                        BuildingServiceRules.EducationSeverity(c, buildings[home]) : 0;
                    if (education == 0) needs &= ~(1 << 10);
                    if (education > 1) sample.Severe |= 1 << 10;
                }
                sample.Harms |= needs;
                if (!housing) sample.Severe |= 1 << 8;
                Merge(ref sample, c.m_workBuilding, buildings);
                if (c.CurrentLocation == Citizen.Location.Visit) Merge(ref sample, c.m_visitBuilding, buildings);
                // Utility shortage counters on Citizen retain history until ResidentAI next updates.
                // Current home/work/visit building notifications above are the source for active outages.
                int health = ComplaintMapping.HealthSeverity(c, sample.Severe);
                // Known homes use the same household health grade as the building info view.
                // Citizens without a usable home still retain their own real health problems.
                if (!hasHome && health > 0) sample.Harms |= 1 << 6;
                if (!hasHome && health > 1) sample.Severe |= 1 << 6;
                if (!ComplaintMapping.MedicalServiceHarm((sample.Needs & (1 << 11)) != 0, health))
                {
                    sample.Harms &= ~(1 << 11);
                    sample.Severe &= ~(1 << 11);
                }
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
            sample.Harms |= samples[id].Harms; sample.Severe |= samples[id].Severe;
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
            mask &= ~((1 << 4) | (1 << 5)); severe &= ~((1 << 4) | (1 << 5));
            Sample result = new Sample { Harms = mask, Severe = severe };
            if (Suitable(b) && b.Info != null && b.Info.m_buildingAI is CommonBuildingAI)
            {
                AddState(ref result, 4, BuildingServiceRules.GarbageSeverity(b.m_garbageBuffer));
                if (Singleton<UnlockManager>.instance.Unlocked(ItemClass.Service.PoliceDepartment))
                    AddState(ref result, 5, BuildingServiceRules.CrimeSeverity(b.m_crimeBuffer, b.m_citizenCount));
                if (b.Info.m_class.m_service == ItemClass.Service.Residential)
                    AddState(ref result, 6, BuildingServiceRules.HealthSeverity(b.m_health, b.m_citizenCount));
                int hazard, size, tolerance;
                if (Singleton<UnlockManager>.instance.Unlocked(ItemClass.Service.FireDepartment) &&
                    b.Info.m_buildingAI.GetFireParameters(id, ref b, out hazard, out size, out tolerance))
                {
                    byte district = Singleton<DistrictManager>.instance.GetDistrict(b.m_position);
                    bool detectors = (Singleton<DistrictManager>.instance.m_districts.m_buffer[district].m_servicePolicies & DistrictPolicies.Services.SmokeDetectors) != 0;
                    int coverage = Singleton<CoverageManager>.instance.FindFireCoverage(b.CalculateAccessPosition(0f, 0f));
                    AddState(ref result, 14, BuildingServiceRules.FireSeverity(hazard, size, tolerance, coverage, detectors));
                }
            }
            // Congestion is a separate proxy; a building's red power icon must not amplify it.
            NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
            ushort road = b.m_accessSegment;
            if (road != 0 && road < roads.Length &&
                (roads[road].m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) == NetSegment.Flags.Created &&
                roads[road].Info != null && roads[road].Info.m_class.m_service == ItemClass.Service.Road && roads[road].m_trafficDensity >= 80) AddState(ref result, 2, roads[road].m_trafficDensity >= 95 ? 2 : 1);
            if (b.m_fireIntensity > 0) AddState(ref result, 14, 2);
            return result;
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
        internal static int FireSeverity(int hazard, int size, int tolerance, int coverage, bool detectors)
        {
            if (hazard < 0 || size < 0 || tolerance < 0) return 0;
            if (detectors) hazard = hazard * 75 / 100;
            int good = (int)Math.Min(100L, (10L + tolerance) * (25 + coverage * 100 / 255) * 2000 /
                ((100L + hazard) * (100L + size)));
            return Severity(100 - good, 100);
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
