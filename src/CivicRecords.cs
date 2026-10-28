using System;
using System.IO;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    internal sealed class RallyNotice
    {
        internal int Origin, Reason;
        internal long Due, Since;
        internal string Area, Road;
    }
    internal sealed class IncidentReport
    {
        internal int Origin, Fires, Collapses, Roads, Failures, Cars;
        internal long Started, Ended, ProblemSince, RiotAt;
        internal bool Manual, Partial;
        internal int[] DemandBaseline;
        internal long DemandCaptured;
        internal bool DemandLate;
        internal string Area = "", Cause = "", Road = "", Outcome = "";
        internal bool CasualtiesKnown, CasualtiesPartial;
        internal int RioterInjured, RioterDead, PoliceInjured, PoliceDead;
        internal string CasualtyText()
        {
            if (!CasualtiesKnown) return L.T("\n사상자: 미기록");
            return string.Format(L.T("\n폭동대: 부상 {0}명 · 사망 {1}명\n경찰: 부상 {2}명 · 사망 {3}명"), RioterInjured, RioterDead, PoliceInjured, PoliceDead) +
                (CasualtiesPartial ? L.T("\n사상자 부분 기록: 불러오기 전후 참가자는 별도로 집계") : "");
        }
    }
    // One token per combatant, retained through treatment. Captured report prevents cross-event writes.
    internal sealed class IncidentCasualty
    {
        private readonly IncidentReport report;
        private bool injured, dead;
        internal IncidentCasualty(IncidentReport owner) { report=owner; }
        internal void Record(bool police, bool fatal)
        {
            if (report==null) return;
            lock (report) {
                if (report.Ended!=0 || dead || (injured && !fatal)) return;
                report.CasualtiesKnown=true;
                int injuryDelta=fatal ? (injured ? -1 : 0) : 1;
                if (police) { report.PoliceInjured+=injuryDelta; if(fatal) report.PoliceDead++; }
                else { report.RioterInjured+=injuryDelta; if(fatal) report.RioterDead++; }
                if(fatal) dead=true; else injured=true;
            }
        }
    }
    internal static class CivicRecords
    {
        internal static void Rebase(long now, double factor)
        {
            if (Notice != null) {
                Notice.Due = GameClock.Rebase(Notice.Due, now, factor);
                Notice.Since = GameClock.Rebase(Notice.Since, now, factor);
            }
            if (Draft != null) {
                Draft.Started = GameClock.Rebase(Draft.Started, now, factor);
                Draft.ProblemSince = GameClock.Rebase(Draft.ProblemSince, now, factor);
                Draft.RiotAt = GameClock.Rebase(Draft.RiotAt, now, factor);
                Draft.DemandCaptured = GameClock.Rebase(Draft.DemandCaptured, now, factor);
            }
            for (int i = 0; i < penalized.Length; i++) penalized[i] = GameClock.Rebase(penalized[i], now, factor);
        }
        internal const string Key = "CivicPressure.AdministrationProgress";
        internal static long ReportRevision;
        internal static RallyNotice Notice;
        // Cause for the next incident report (set before a requested protest, e.g. facility opposition). Not saved.
        internal static string NextCause;
        internal static volatile IncidentReport Draft;
        private static readonly List<IncidentReport> reports = new List<IncidentReport>();
        private static readonly int[] trust = InitialTrust();
        private static readonly long[] penalized = new long[256 * ComplaintRules.Slots];
        private static int[] InitialTrust() { int[] values=new int[256]; for(int i=0;i<values.Length;i++) values[i]=50;return values; }
        internal static int Trust(int region) { return region>0 && region<256 ? trust[region] : 50; }
        internal static double Patience(int region) { return .5 + Trust(region)/100.0; }
        internal static void ChangeTrust(int region,int delta)
        { if(Available && region>0 && region<256) trust[region]=Math.Max(0,Math.Min(100,trust[region]+delta)); }
        internal static void Deadline(ComplaintRecord c, CityView city, long now)
        {
            if(!Available || c.Reason==ComplaintRules.OddReason || !c.Open || c.ImprovingSince>0 || now<c.Deadline || c.Region<=0 || city.Regions[c.Region]==null || city.Regions[c.Region].OfficeId==0) return;
            int key=c.Region*ComplaintRules.Slots+c.Reason;
            if(penalized[key]==c.Since) return;
            penalized[key]=c.Since;CivicStanding.Missed();ChangeTrust(c.Region,c.Promised ? -8 : -3);
        }
        internal static void Maintain(CityView city)
        {
            if(!Available) return;
            for(int i=1;i<256;i++) if(city.Regions[i]==null) {trust[i]=50;Array.Clear(penalized,i*ComplaintRules.Slots,ComplaintRules.Slots);}
        }
        private static byte[] preserved;
        internal static string Error = "";
        internal static bool Available { get { return preserved == null; } }
        internal static void Reset() { ReportRevision++; Notice = null; Draft = null; reports.Clear(); preserved = null; Error=""; for(int i=0;i<256;i++) trust[i]=50; Array.Clear(penalized,0,penalized.Length); }
        internal static void Begin(UnrestEvent e, CityView city, DateTime now, bool partial)
        {
            if (!Available) return;
            RallyNotice n = Notice;
            Draft = new IncidentReport { Origin = e.Origin, Started = now.Ticks, Manual = e.Manual, Partial = partial,
                Area = e.Origin < 0 ? city.CityName : city.Regions[e.Origin] == null ? L.T("변경된 지구") : city.Regions[e.Origin].Name,
                Cause = NextCause != null ? NextCause : e.Manual ? L.T("수동 발생") : n == null ? L.T("기존 사건 · 원인 미기록") : Happiness.Reasons[n.Reason],
                ProblemSince = n == null ? 0 : n.Since, Road = n == null ? L.T("주요 피해 도로 미기록") : n.Road, RiotAt = e.Riot ? now.Ticks : 0 };
            NextCause = null;
            Draft.CasualtiesKnown = true;
            Draft.CasualtiesPartial = partial;
            ProtestDemands.Capture(Draft, city, now, partial);
        }
        internal static void Observe(UnrestEvent e, CityView city, DateTime now)
        {
            if (!Available) return;
            if (Draft == null) Begin(e, city, now, true);
            ProtestDemands.Capture(Draft, city, now, true);
            Draft.Manual |= e.Manual;
            if (e.Riot && Draft.RiotAt == 0) Draft.RiotAt = now.Ticks;
            Draft.Roads = e.RoadDamage;
        }
        internal static void Finish(UnrestEvent e, CityView city, DateTime now, string outcome)
        {
            if (!Available) return;
            Observe(e,city,now); Commit(now.Ticks,outcome);
            CivicChirper.PostText(L.T("사건 종료 보고서가 등록되었습니다. 도시 행정 '기록'에서 열람하실 수 있습니다. #사건보고서"));
        }
        internal static void Commit(long now,string outcome)
        {
            if(Draft==null) return;
            lock(Draft) { Draft.Ended=Math.Max(Draft.Started,now); Draft.Outcome=outcome; }
            ReportRevision++; reports.Insert(0,Draft); if(reports.Count>20) reports.RemoveAt(20); Draft=null;
        }
        internal static string[] ReportTexts()
        {
            List<string> result = new List<string>();
            foreach (IncidentReport r in reports) result.Add(r.Area + " · " + (r.RiotAt > 0 ? L.T("폭동") : L.T("시위")) + L.T(" 종료 보고서\n\n") +
                L.T("원인: ") + r.Cause + L.T("\n주요 도로: ") + r.Road + "\n" + new DateTime(r.Started).ToString("yyyy-MM-dd HH:mm") + " ~ " + new DateTime(r.Ended).ToString("MM-dd HH:mm") + "\n\n" +
                L.T("기록된 지속 시간: ") + Math.Max(0,(r.Ended-r.Started)/(double)GameClock.HourTicks).ToString("0.0") + L.T(" 게임 시간\n") +
                (r.ProblemSince > 0 ? L.T("문제 감지~종료: ") + Math.Max(0,(r.Ended-r.ProblemSince)/(double)GameClock.HourTicks).ToString("0.0") + L.T(" 게임 시간\n") : "") +
                L.T("화재 ") + r.Fires + L.T("건 · 직접 붕괴 ") + r.Collapses + L.T("곳 · 도로 파괴 ") + r.Roads + L.T("구간\n경찰차 출동 ") + r.Cars + L.T("대 · 진압 실패 ") + r.Failures + L.T("회\n\n결과: ") + r.Outcome +
                r.CasualtyText() + (r.Partial ? L.T("\n부분 기록: 이전 피해 내역은 일부 누락될 수 있음") : "") + (r.Manual ? L.T("\n수동 시험 사건") : ""));
            return result.ToArray();
        }
        private static void WriteReport(BinaryWriter w, IncidentReport r)
        {
            w.Write(r.Origin); w.Write(r.Fires); w.Write(r.Collapses); w.Write(r.Roads); w.Write(r.Failures); w.Write(r.Cars);
            w.Write(r.Started); w.Write(r.Ended); w.Write(r.ProblemSince); w.Write(r.RiotAt); w.Write(r.Manual); w.Write(r.Partial);
            w.Write(r.Area); w.Write(r.Cause); w.Write(r.Road); w.Write(r.Outcome);
            w.Write(r.DemandBaseline != null);
            if (r.DemandBaseline != null) {
                w.Write(r.DemandCaptured); w.Write(r.DemandLate); w.Write(r.DemandBaseline.Length);
                foreach (int count in r.DemandBaseline) w.Write(count);
            }
            lock(r) {
                w.Write(r.CasualtiesKnown); w.Write(r.CasualtiesPartial);
                w.Write(r.RioterInjured); w.Write(r.RioterDead); w.Write(r.PoliceInjured); w.Write(r.PoliceDead);
            }
        }
        private static IncidentReport ReadReport(BinaryReader r, int version)
        {
            IncidentReport x = new IncidentReport { Origin=r.ReadInt32(), Fires=r.ReadInt32(), Collapses=r.ReadInt32(), Roads=r.ReadInt32(), Failures=r.ReadInt32(), Cars=r.ReadInt32(),
                Started=r.ReadInt64(), Ended=r.ReadInt64(), ProblemSince=r.ReadInt64(), RiotAt=r.ReadInt64(), Manual=r.ReadBoolean(), Partial=r.ReadBoolean(),
                Area=r.ReadString(), Cause=r.ReadString(), Road=r.ReadString(), Outcome=r.ReadString() };
            if(x.Origin < -1 || x.Origin > 255 || x.Fires < 0 || x.Fires > 48 || x.Collapses < 0 || x.Collapses > 48 || x.Roads < 0 || x.Roads > 8 || x.Failures < 0 || x.Cars < 0 || x.Area.Length>256 || x.Cause.Length>512 || x.Road.Length>512 || x.Outcome.Length>512) throw new InvalidDataException("Invalid report");
            foreach(long time in new long[]{x.Started,x.Ended,x.ProblemSince,x.RiotAt}) if(time<0 || time>DateTime.MaxValue.Ticks) throw new InvalidDataException("Invalid report time");
            if(x.Ended != 0 && x.Ended < x.Started) throw new InvalidDataException("Report time order");
            if (version >= 6 && r.ReadBoolean()) {
                x.DemandCaptured = r.ReadInt64(); x.DemandLate = r.ReadBoolean(); int size = r.ReadInt32();
                if (size != (version == 6 ? 21 : Happiness.Weights.Length + 3) || x.DemandCaptured < 0 || x.DemandCaptured > DateTime.MaxValue.Ticks) throw new InvalidDataException("Invalid demand baseline");
                x.DemandBaseline = new int[Happiness.Weights.Length + 3];
                for (int i = 0; i < size; i++) { int target = version == 6 && i >= 18 ? Happiness.Weights.Length + i - 18 : i; x.DemandBaseline[target] = r.ReadInt32(); if (x.DemandBaseline[target] < 0) throw new InvalidDataException("Invalid demand count"); }
            }
            if(version>=8) {
                x.CasualtiesKnown=r.ReadBoolean(); x.CasualtiesPartial=r.ReadBoolean();
                x.RioterInjured=r.ReadInt32(); x.RioterDead=r.ReadInt32(); x.PoliceInjured=r.ReadInt32(); x.PoliceDead=r.ReadInt32();
                foreach(int count in new int[]{x.RioterInjured,x.RioterDead,x.PoliceInjured,x.PoliceDead})
                    if(count<0 || count>1000000) throw new InvalidDataException("Invalid casualty count");
            }
            return x;
        }
        internal static byte[] Save()
        {
            if (preserved != null) return preserved;
            using (MemoryStream stream = new MemoryStream()) using (BinaryWriter w = new BinaryWriter(stream)) {
                w.Write(0x43505231); w.Write(8); w.Write(Notice != null);
                if (Notice != null) { w.Write(Notice.Origin); w.Write(Notice.Reason); w.Write(Notice.Due); w.Write(Notice.Since); w.Write(Notice.Area); w.Write(Notice.Road); }
                w.Write(Draft != null); if(Draft != null) WriteReport(w,Draft);
                w.Write(reports.Count); foreach(IncidentReport report in reports) WriteReport(w,report);
                for(int i=0;i<256;i++) w.Write((byte)trust[i]);
                w.Write(penalized.Length); foreach(long stamp in penalized) w.Write(stamp);
                return stream.ToArray();
            }
        }
        internal static void Load(byte[] data)
        {
            Reset();
            if (data == null || data.Length == 0) return;
            try {
                if (data.Length > 100000) throw new InvalidDataException("Oversized progress");
                using (BinaryReader r = new BinaryReader(new MemoryStream(data,false))) {
                    if (r.ReadInt32() != 0x43505231) throw new InvalidDataException("Unknown progress");
                    int version=r.ReadInt32(); if(version<1 || version>8) throw new InvalidDataException("Unknown progress version");
                    if (r.ReadBoolean()) {
                        Notice = new RallyNotice { Origin = r.ReadInt32(), Reason = r.ReadInt32(), Due = r.ReadInt64(), Since = r.ReadInt64(), Area = r.ReadString(), Road = r.ReadString() };
                        if (Notice.Origin < -1 || Notice.Origin > 255 || Notice.Reason < 0 || Notice.Reason >= (version <= 3 ? 15 : version == 4 ? 17 : version <= 6 ? 18 : ComplaintRules.ReasonCount) || Notice.Due < 0 || Notice.Due > DateTime.MaxValue.Ticks || Notice.Since < 0 || Notice.Since > Notice.Due || Notice.Area.Length > 256 || Notice.Road.Length > 512) throw new InvalidDataException("Invalid notice");
                    }
                    if(version>=2) {
                        if(r.ReadBoolean()) { Draft=ReadReport(r, version); Draft.CasualtiesPartial=true; }
                        int count=r.ReadInt32(); if(count<0 || count>20) throw new InvalidDataException("Too many reports");
                        for(int i=0;i<count;i++) reports.Add(ReadReport(r, version));
                    }
                    if(version>=3) {
                        for(int i=0;i<256;i++) {trust[i]=r.ReadByte();if(trust[i]>100) throw new InvalidDataException("Invalid trust");}
                        int ledgerSlots = version == 3 ? 16 : version == 4 ? 18 : version <= 6 ? 19 : ComplaintRules.Slots;
                        if(r.ReadInt32()!=256*ledgerSlots) throw new InvalidDataException("Invalid trust ledger");
                        for(int i=0;i<256*ledgerSlots;i++) {long stamp=r.ReadInt64();if(stamp<0 || stamp>DateTime.MaxValue.Ticks) throw new InvalidDataException("Invalid trust stamp");
                            int reason=i%ledgerSlots; if((version==3 && reason==15) || (version==4 && reason==17) || (version>=5 && version<=6 && reason==18)) reason=ComplaintRules.OddReason;
                            penalized[(i/ledgerSlots)*ComplaintRules.Slots+reason]=stamp;}
                    }
                    if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing progress");
                }
            } catch (Exception error) { Reset(); preserved = data; Error=L.T("행정 진행 저장 오류 · 원본 보존 중: ") + error.Message; }
        }
        internal static bool Needed(RallyNotice n, CityView city)
        {
            RegionView region = n.Origin < 0 ? city.All : city.Regions[n.Origin];
            if (!city.HasCityHall || region == null || !UnrestRules.CanProtest(n.Origin < 0 ? city.CityScore : region.Score)) return false;
            if(n.Origin>=0 && CivicComplaints.Local(n.Reason)) {
                if(ComplaintRules.Recovered(n.Reason,CivicComplaints.Affected(region,n.Origin,n.Reason),CivicComplaints.Severe(region,n.Origin,n.Reason),CivicComplaints.Population(region,n.Origin,n.Reason)))return false;
            }
            else if (ComplaintRules.Recovered(n.Reason, region.Affected[n.Reason], region.Severe[n.Reason], region.Residents)) return false;

            return true;
        }
        internal static string NoticeText(CityView city, DateTime now)
        {
            RallyNotice n = Notice;
            if (n == null) return "";
            RegionView region = n.Origin > 0 ? city.Regions[n.Origin] : null;
            string hall = region != null && region.OfficeTransferred ? region.ServiceOffice : region != null && region.OfficeId != 0 ? region.Office : city.CityHall;
            return n.Area + " · " + Happiness.Reasons[n.Reason] + "\n" + n.Road + "\n" + hall + L.T(" 집회 예정 · ") + Math.Max(0,(n.Due-now.Ticks)/(double)GameClock.HourTicks).ToString("0.0") + L.T(" 게임 시간 후");
        }
    }
}
