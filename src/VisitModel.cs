using System;

namespace CivicPressure
{
    // A resident walking or riding to the responsible office to file a complaint in person. Not saved: after a reload
    // the complaint simply sends a new visitor.
    internal sealed class VisitRecord
    {
        internal int Key;
        internal uint Citizen;
        internal ushort Home, Office;
        internal long Started;
        // Last seen within VisitRules.ArrivalRadius of the office while travelling.
        internal bool Near;
    }

    // A resident going to an office for everyday paperwork (no complaint). Only for the look of daily life; not saved.
    internal sealed class RoutineVisit
    {
        internal uint Citizen;
        internal ushort Office;
        internal long Started;
        internal bool Near;
        // What this resident says they are here for (shown on the pin): index into the city hall or district office list.
        internal int Purpose;
        internal bool Hall;
    }

    // One visitor the mod sent, for the map pins (subscriber feedback 鱼塘救世主 2026-09-18: tell mod visits apart from
    // ordinary citizens). Kind: 0 complaint, 1 everyday paperwork, 2 disturbance.
    internal sealed class VisitorMark
    {
        internal uint Citizen;
        internal ushort Office;
        internal int Kind;
        internal int Purpose;
        internal bool Hall;
    }

    // A troublemaker causing a disturbance at an office. Not saved either.
    internal sealed class Disturbance
    {
        internal ushort Office;
        internal int Region;
        internal uint Citizen;
        internal long Started, PoliceArrived, NextRequest;
        internal ushort PoliceVehicle;
    }

    internal enum VisitStatus { Travelling, Arrived, Failed }
    internal enum DisturbanceStatus { WaitingPolice, PoliceOnScene, Resolved, NoPolice }

    // Pure rules: no game or Unity calls, so the automated checks can run them.
    internal static class VisitRules
    {
        // Complaints wait this long for the visitor before they are filed the usual way (road cut, long trip).
        internal const double TimeoutHours = 12;
        internal const int MaxVisits = 8;
        internal const double TroubleChance = .08;
        internal const double TroubleCooldownHours = 24;
        // Police stay this long after arriving before the office is calm again (15 game minutes: about 1.7 real minutes with
        // Real Time, 15 seconds without; 1.5 hours felt far too long in game, 2026-09-17).
        internal const double OnSceneHours = .25;
        internal const double PoliceWaitHours = 6;
        internal const double RequestIntervalHours = .5;
        // Without districts, only homes this close to the city hall visit in person (metres).
        internal const float CityHallRadius = 1500f;
        // Offices without visitor slots send arrivals straight home, so reaching the entrance also counts (metres).
        internal const float ArrivalRadius = 100f;

        // Everyday administrative visits (subscriber feedback: "citizens sometimes need to visit district offices or
        // City Hall ... as part of their daily routines"). Rate per 1,000 served residents per game day, office hours only.
        internal const double RoutinePerThousandPerDay = 12;
        internal const double MaxRoutineBudget = 3;
        internal const int MaxRoutine = 24, MaxRoutinePerOffice = 6;
        internal const float OfficeOpenHour = 9f, OfficeCloseHour = 18f;
        // Homes in districts without their own office use the city hall when this close (metres).
        internal const float RoutineHallRadius = 3000f;

        internal static bool OfficeOpen(float hourOfDay) { return hourOfDay >= OfficeOpenHour && hourOfDay < OfficeCloseHour; }

        // hours: game hours since the last update (capped at one so a pause or reload does not release a crowd).
        internal static double RoutineBudget(double budget, int servedResidents, double hours)
        {
            if (hours > 0 && servedResidents > 0) budget += servedResidents / 1000.0 * RoutinePerThousandPerDay * Math.Min(hours, 1) / 24;
            return Math.Max(0, Math.Min(MaxRoutineBudget, budget));
        }

        // The office a home uses: its district office, else the city hall when near enough, else none (0).
        internal static ushort RoutineOffice(ushort districtOffice, ushort hall, float distanceToHall)
        {
            return RoutineOffice(districtOffice, hall, distanceToHall, false);
        }

        // Some errands (passports, business registration) belong to the city hall, so a share of the everyday visits goes
        // there even from districts with their own office (user question 2026-09-18: city hall visits were never seen).
        internal static ushort RoutineOffice(ushort districtOffice, ushort hall, float distanceToHall, bool hallErrand)
        {
            bool hallReachable = hall != 0 && distanceToHall <= RoutineHallRadius;
            if (hallErrand && hallReachable) return hall;
            if (districtOffice != 0) return districtOffice;
            return hallReachable ? hall : (ushort)0;
        }

        // One visit in four is city hall business when the city hall is close enough.
        internal const double HallErrandShare = .25;
        internal static bool HallErrand(double roll) { return roll < HallErrandShare; }

        // How many different reasons a visitor can give, per destination (the texts live in the UI).
        internal const int HallPurposes = 7, OfficePurposes = 10;
        internal static int Purpose(bool hall, double roll)
        {
            int count = hall ? HallPurposes : OfficePurposes;
            int index = (int)(roll * count);
            return index < 0 ? 0 : index >= count ? count - 1 : index;
        }

        // The complaint's intake time has come (the moment it would open without a visit).
        internal static bool IntakeDue(ComplaintRecord c, int affected, int severe, int residents, long now)
        {
            if (c.Open || c.Reason == ComplaintRules.OddReason || affected <= 0 || residents <= 0 || c.Since == 0 || now < c.QuietUntil) return false;
            if (ComplaintRules.Rate(affected, severe, residents) <= ComplaintRules.OpenRatio(c.Reason)) return false;
            return now - c.Since >= GameClock.Hours(ComplaintRules.IntakeHours(c.Reason));
        }

        // officeDistrict: the complaint's district has its own district office.
        // hallDistrict: the city hall stands in the complaint's district; outside any district the home must also be near.
        internal static bool InPerson(bool officeDistrict, bool hallDistrict, bool outsideDistricts, float distanceToHall)
        {
            if (officeDistrict) return true;
            if (!hallDistrict) return false;
            return !outsideDistricts || distanceToHall <= CityHallRadius;
        }

        internal static VisitStatus Progress(bool movingToOffice, bool visitingOffice, bool wasNear, long started, long now)
        {
            if (visitingOffice || (!movingToOffice && wasNear)) return VisitStatus.Arrived;
            if (!movingToOffice || now - started >= GameClock.Hours(TimeoutHours)) return VisitStatus.Failed;
            return VisitStatus.Travelling;
        }

        internal static bool Troublemaker(double roll, long now, long lastTrouble)
        {
            return roll < TroubleChance && (lastTrouble == 0 || now < lastTrouble || now - lastTrouble >= GameClock.Hours(TroubleCooldownHours));
        }

        internal static DisturbanceStatus Status(Disturbance d, long now)
        {
            if (d.PoliceArrived > 0)
                return now - d.PoliceArrived >= GameClock.Hours(OnSceneHours) ? DisturbanceStatus.Resolved : DisturbanceStatus.PoliceOnScene;
            return now - d.Started >= GameClock.Hours(PoliceWaitHours) ? DisturbanceStatus.NoPolice : DisturbanceStatus.WaitingPolice;
        }

        internal static bool RequestPolice(Disturbance d, bool policeOnTheWay, long now)
        {
            return d.PoliceArrived == 0 && !policeOnTheWay && now >= d.NextRequest;
        }
    }
}
