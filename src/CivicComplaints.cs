using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    internal sealed class RoadResidents
    {
        internal ushort Road;
        internal byte Region;
        internal uint BuildIndex;
        internal int Residents;
        internal readonly int[] Affected = new int[ComplaintRules.ReasonCount], Severe = new int[ComplaintRules.ReasonCount];
    }
    internal enum ComplaintKind { Complaint = 0, Opposition = 1, Suggestion = 2 }
    internal sealed class ComplaintView
    {
        internal int Key, Region;
        internal long Stamp;
        internal string Title, Detail, Road, Stage, LocationLabel;
        internal double Hours;
        internal bool CanPromise, CanReject, CanAgree, HasRoad, Odd, Improving; internal ComplaintHome Home; internal ComplaintSite Site; internal ComplaintHotspot Hotspot;
        internal uint Visitor;
        internal ComplaintKind Kind;
        internal Vector3 Position;
    }
    internal sealed class ComplaintMarker
    {
        internal int Key, Region;
        internal long Stamp;
        internal string Text;
        internal Vector3 Position;
        internal bool Office, Severe;
        internal double Priority;
    }
    internal static partial class CivicComplaints
    {
        internal static void Rebase(long now, double factor)
        {
            foreach (ComplaintRecord c in records.Values) {
                c.Since = GameClock.Rebase(c.Since, now, factor);
                c.Deadline = GameClock.Rebase(c.Deadline, now, factor);
                c.QuietUntil = GameClock.Rebase(c.QuietUntil, now, factor);
                c.OfficeUntil = GameClock.Rebase(c.OfficeUntil, now, factor);
                c.ImprovingSince = GameClock.Rebase(c.ImprovingSince, now, factor);
            }
            nextOddCheck = GameClock.Rebase(nextOddCheck, now, factor);
        }
        // Simulation/save/UI serialization follows CivicSimulation.Gate; UI sees immutable snapshots.
        private static readonly Dictionary<int, ComplaintRecord> records = new Dictionary<int, ComplaintRecord>();
        private static byte[] preserved;
        private static int newsBudget = 3, groupedNews;
        private static readonly System.Random random = new System.Random();
        private static long nextOddCheck;
        private static readonly Dictionary<int,ComplaintHome> homes=new Dictionary<int,ComplaintHome>(); private static readonly int[,] localAffected=new int[256,2], localSevere=new int[256,2];
        private static readonly Dictionary<int,ComplaintSite> sites=new Dictionary<int,ComplaintSite>();
        internal static bool Local(int reason) { return reason==2 || reason==17; }
        internal static int Affected(RegionView area,int region,int reason)
        { return area==null ? 0 : Local(reason) ? localAffected[region,reason==2?0:1] : area.Affected[reason]; }
        internal static int Severe(RegionView area,int region,int reason)
        { return area==null ? 0 : Local(reason) ? localSevere[region,reason==2?0:1] : area.Severe[reason]; }
        internal static int Population(RegionView area,int region,int reason)
        { return area==null ? 0 : Local(reason) ? Math.Max(area.Residents,Affected(area,region,reason)) : area.Residents; }
        private static readonly Dictionary<int,ComplaintHotspot> hotspots=new Dictionary<int,ComplaintHotspot>();
        private static readonly Dictionary<int, RoadResidents[]> worst = new Dictionary<int, RoadResidents[]>();
        internal static string Error { get { return preserved == null ? "" : L.T("민원 저장 데이터 오류 · 원본 보존 중"); } }
        internal static void Reset() { records.Clear(); CivicVisits.Reset(); CivicStanding.Reset(); worst.Clear();hotspots.Clear();homes.Clear();sites.Clear(); Array.Clear(localAffected,0,localAffected.Length); Array.Clear(localSevere,0,localSevere.Length); preserved = null; nextOddCheck = 0; }
        internal static void Load(byte[] bytes)
        {
            try { foreach (ComplaintRecord c in ComplaintSave.Decode(bytes)) records[c.Region * ComplaintRules.Slots + c.Reason] = c; }
            catch (Exception e) { records.Clear(); preserved = bytes; Debug.LogError("[CivicPressure] Complaint save preserved: " + e); }
        }
        internal static byte[] Save()
        {
            if (preserved != null) return preserved;
            ComplaintRecord[] copy = new ComplaintRecord[records.Count]; records.Values.CopyTo(copy, 0);
            // Experimental complaint stays session-only, like the combat actors. No new save variant.
            List<ComplaintRecord> saved=new List<ComplaintRecord>(copy);
            saved.Remove(devExcess); copy=saved.ToArray();
            return ComplaintSave.Encode(copy);
        }
        internal static void Census(CityCensus census)
        {
            worst.Clear();hotspots.Clear();homes.Clear();sites.Clear(); Array.Clear(localAffected,0,localAffected.Length); Array.Clear(localSevere,0,localSevere.Length);
            if(census.Homes!=null)foreach(KeyValuePair<int,ComplaintHome> home in census.Homes)homes.Add(home.Key,home.Value);
            if(census.Sites!=null)foreach(KeyValuePair<int,ComplaintSite> site in census.Sites)sites.Add(site.Key,site.Value);
            foreach(ComplaintHotspot spot in census.Hotspots.Values){
                int slot=spot.Reason==2?0:1;
                localAffected[spot.Region,slot]+=spot.Affected;localSevere[spot.Region,slot]+=spot.Severe;
                int key=spot.Region*ComplaintRules.Slots+spot.Reason;ComplaintHotspot old;
                if(!hotspots.TryGetValue(key,out old) || ComplaintHotspot.Better(spot,old))hotspots[key]=spot;
            }
            foreach (RoadResidents road in census.Roads.Values)
            {
                for (int reason = 0; reason < ComplaintRules.ReasonCount; reason++)
                {
                    if (reason==2 || reason==17 || road.Affected[reason] == 0) continue;
                    int key = road.Region * ComplaintRules.Slots + reason;
                    RoadResidents[] top;
                    if (!worst.TryGetValue(key, out top)) { top = new RoadResidents[3]; worst.Add(key, top); }
                    RoadResidents candidate = road;
                    for (int i = 0; i < 3 && candidate != null; i++)
                    {
                        if (top[i] == null || Priority(candidate, reason) > Priority(top[i], reason))
                        { RoadResidents old = top[i]; top[i] = candidate; candidate = old; }
                    }
                }
            }
        }
        private static double Priority(RoadResidents r, int reason) { return ComplaintRules.RoadPriority(r.Affected[reason], r.Severe[reason], r.Residents); }
        internal static ComplaintMarker[] Markers(CityView city)
        {
            List<ComplaintMarker> list=new List<ComplaintMarker>();
            foreach(ComplaintRecord c in records.Values) {
                if(!c.Open || c.Reason==ComplaintRules.OddReason) continue;
                if(c.Reason==2 || c.Reason==17){
                    ComplaintHotspot spot;
                    if(hotspots.TryGetValue(c.Region*ComplaintRules.Slots+c.Reason,out spot) && spot.Live())
                        list.Add(new ComplaintMarker{Key=c.Region*ComplaintRules.Slots+c.Reason,Region=c.Region,Stamp=c.Since,
                            Position=spot.Position,Text=spot.Label()+L.T("\n피해 ")+spot.Affected+L.T("명"),Severe=spot.Severe>0,Priority=spot.Affected+.5*spot.Severe});
                    continue;
                }
                RoadResidents[] top;
                if(!worst.TryGetValue(c.Region*ComplaintRules.Slots+c.Reason,out top)) continue;
                foreach(RoadResidents road in top) if(LiveRoad(road)) {
                    list.Add(new ComplaintMarker { Key=c.Region*ComplaintRules.Slots+c.Reason,Region=c.Region,Stamp=c.Since,
                        Position=UnrestRoads.Curve(Singleton<NetManager>.instance.m_segments.m_buffer[road.Road]).Position(.5f),
                        Text=Happiness.Reasons[c.Reason]+" · "+road.Affected[c.Reason]+L.T("명\n")+Singleton<NetManager>.instance.GetSegmentName(road.Road),
                        Severe=road.Severe[c.Reason]>0,Priority=Priority(road,c.Reason) });
                }
            }
            list.Sort(delegate(ComplaintMarker a,ComplaintMarker b){return b.Priority.CompareTo(a.Priority);});
            if(list.Count>12) list.RemoveRange(12,list.Count-12);
            List<ComplaintMarker> offices=new List<ComplaintMarker>(); List<ushort> used=new List<ushort>();
            foreach(ComplaintMarker marker in list) {
                RegionView area=city.Regions[marker.Region];
                ushort hall=area!=null && area.OfficeTransferred ? area.ServiceOfficeId : area!=null && area.OfficeId!=0 ? area.OfficeId : city.CityHallId;
                if(hall==0 || used.Contains(hall)) continue;
                used.Add(hall); Building b=Singleton<BuildingManager>.instance.m_buildings.m_buffer[hall];
                if(!CivicSimulation.Live(b)) continue;
                offices.Add(new ComplaintMarker {Key=marker.Key,Region=marker.Region,Stamp=marker.Stamp,Office=true,Position=b.m_position,
                    Text=(area!=null && area.OfficeTransferred ? area.ServiceOffice : area!=null && area.OfficeId!=0 ? area.Office : city.CityHall)+L.T("\n담당 행정청")});
            }
            list.AddRange(offices);return list.ToArray();
        }
        internal static void Tick(CityView city, DateTime now)
        {
            if (preserved != null || city.All == null) return;
#if DEV
            DevTick(city, now);
#endif
            CivicRecords.Maintain(city); ComplaintRecurrence.Maintain(city);
            newsBudget = 3; groupedNews = 0;
            for (int region = 0; region < 256; region++)
            {
                RegionView area = city.Regions[region];
                for (int reason = 0; reason < ComplaintRules.ReasonCount; reason++)
                {
                    int key = region * ComplaintRules.Slots + reason;
                    ComplaintRecord c;
                    bool problem = Affected(area,region,reason) > 0;
                    if (!records.TryGetValue(key, out c))
                    {
                        if (!problem) continue;
                        c = new ComplaintRecord { Region = region, Reason = reason }; records.Add(key, c);
                    }
                    bool wasOpen = c.Open;
                    bool wasPromised = c.Promised; long detected = c.Since;
                    // In-person complaints: near the office the complaint opens only once a resident arrives (CivicVisits).
                    if (!c.Open)
                    {
                        ComplaintHome visitor;
                        homes.TryGetValue(key, out visitor);
                        if (VisitRules.IntakeDue(c, Affected(area,region,reason), Severe(area,region,reason), Population(area,region,reason), now.Ticks))
                        { if (CivicVisits.Hold(key, c, city, visitor, now.Ticks)) continue; }
                        else CivicVisits.Cancel(key);
                    }
                    bool visited = !wasOpen && CivicVisits.ConsumeArrival(key);
                    if (ComplaintRules.UpdateCounts(c, Affected(area,region,reason), Severe(area,region,reason), Population(area,region,reason), now.Ticks))
                    {
                        if(wasOpen && area!=null) ComplaintRecurrence.Solved(region,reason,now.Ticks);
                        if(!wasOpen) { int penalty=ComplaintRecurrence.Opened(c,now.Ticks); if(area!=null && area.OfficeId!=0) CivicRecords.ChangeTrust(region,-penalty); }
                        // Trusted district offices get more response time (CivicStanding). Counted for the 7-day review.
                        if(!wasOpen && c.Open) { if(area!=null && area.OfficeId!=0) c.Deadline = now.Ticks + (long)((c.Deadline - now.Ticks) * StandingRules.DeadlineFactor(CivicRecords.Trust(region))); CivicStanding.Opened(); }
                        if(wasOpen && !c.Open) CivicStanding.Resolved();
                        if(wasOpen) {
                            RegionView historyArea=area;
                            if(area!=null && Local(reason)) {
                                historyArea=new RegionView{Name=area.Name,Office=area.Office,OfficeId=area.OfficeId,ServiceOffice=area.ServiceOffice,ServiceOfficeId=area.ServiceOfficeId,OfficeTransferred=area.OfficeTransferred,Affected=(int[])area.Affected.Clone()};
                                historyArea.Affected[reason]=Affected(area,region,reason);
                            }
                            ComplaintHistory.Resolved(region, reason, detected, now.Ticks, wasPromised, historyArea, city.CityHall);
                        }
                        if(wasOpen && area!=null && area.OfficeId!=0) CivicRecords.ChangeTrust(region,wasPromised ? 4 : 2);
                        News(c, city, wasOpen ? (problem ? L.T("낮은 피해 비율이 3시간 유지되어 개선 확인 후 종결했습니다. 잔여 피해는 비율에 따라 행복도에 반영됩니다.") : L.T("민원이 해결되었습니다.")) : (ComplaintRecurrence.Badge(c).Length>0 ? L.T("동일 문제가 다시 접수되었습니다.")+ComplaintRecurrence.Badge(c)+L.T(". 대응 기한은 ") : visited ? L.T("주민이 행정청을 직접 방문해 민원을 접수했습니다. 대응 기한은 ") : L.T("민원이 접수되었습니다. 대응 기한은 ")) + ((c.Deadline-now.Ticks)/(double)GameClock.HourTicks).ToString("0.#") + L.T("게임 시간입니다."));
                    }
                    CivicRecords.Deadline(c,city,now.Ticks);
                    if (c.Open && c.ImprovingSince == 0 && !c.Warned && now.Ticks >= c.Deadline - GameClock.Hours(6))
                    { c.Warned = true; News(c, city, L.T("민원 대응 기한이 임박했습니다.")); }
                }
            }
            TickOdd(city, now);
            CivicVisits.Tick(city, now.Ticks);
            CivicStanding.Tick(city, now);
            if (groupedNews > 0) CivicChirper.PostComplaint(L.T("그 밖에 ") + groupedNews + L.T("건의 민원 상태가 변경되었습니다. 도시 행정 민원 목록에서 확인하시기 바랍니다. #민원"), city, 0);
        }
        private static ComplaintRecord devExcess;
        internal static void DevOverforce(CityView city,int region,DateTime now)
        {
            if(!Features.OddRequests || random.NextDouble()>=.05) return;
            if(region<0) {
                for(int i=0;i<city.Regions.Length;i++) if(city.Regions[i]!=null && city.Regions[i].Housed>0) { region=i; break; }
            }
            if(region<0 || region>=city.Regions.Length || city.Regions[region]==null || city.Regions[region].Housed<=0) return;
            int key=region*ComplaintRules.Slots+ComplaintRules.OddReason; ComplaintRecord existing;
            if(records.TryGetValue(key,out existing) && existing.Open) return;
            devExcess=new ComplaintRecord { Region=region,Reason=ComplaintRules.OddReason,Open=true,Since=now.Ticks,Deadline=now.Ticks+GameClock.Hours(72) };
            records[key]=devExcess;
            News(devExcess,city,OddText(devExcess));
        }
        private static string OddText(ComplaintRecord c)
        {
            if(c==devExcess) return L.T("폭동 진압 과정에서 과도한 물리력이 사용됐습니다. 조사해 주세요.");
            return ComplaintRules.OddRequests[c.Variant];
        }
        private static string OddAnswer(ComplaintRecord c)
        {
            if(c==devExcess) return L.T("진압 과정에 대한 민원을 접수했으며, 현장 대응 경위를 검토하겠습니다.");
            return ComplaintRules.OddAnswers[c.Variant];
        }
        private static void TickOdd(CityView city, DateTime now)
        {
            bool pending = false;
            long quiet = 0;
            foreach (ComplaintRecord c in records.Values)
            {
                if (c.Reason != ComplaintRules.OddReason) continue;
                // Switched off in the options: open odd requests close quietly, with no notice and no trust effect.
                if (!Features.OddRequests && c.Open) { c.Open = false; c.Promised = false; c.QuietUntil = now.Ticks + GameClock.Hours(2); continue; }
                ComplaintRules.LimitOddCooldown(c, now.Ticks);
                bool inhabited = city.Regions[c.Region] != null && city.Regions[c.Region].Housed > 0;
                if (ComplaintRules.FinishOdd(c, now.Ticks, inhabited))
                {
                    News(c, city, !inhabited ? L.T("거주자가 확인되지 않아 기타 요구를 종결했습니다.") : c.Promised ? OddAnswer(c) : L.T("답변 기한이 지나 기타 요구를 종결했습니다."));
                }
                pending |= c.Open; quiet = Math.Max(quiet, c.QuietUntil);
            }
            if (nextOddCheck == 0) nextOddCheck = (now.Ticks + GameClock.Hours(1));
            bool forced = false;
#if DEV
            forced = System.Threading.Interlocked.Exchange(ref devOdd, 0) != 0 && city.HasCityHall;
#endif
            if (!forced && !Features.OddRequests) return;
            if (!forced && (pending || !city.HasCityHall || now.Ticks < quiet || now.Ticks < nextOddCheck)) return;
            nextOddCheck = (now.Ticks + GameClock.Hours(1));
            if (!forced && random.NextDouble() >= .7) return;
            List<int> regions = new List<int>();
            for (int i = 0; i < city.Regions.Length; i++)
                if (city.Regions[i] != null && city.Regions[i].Housed > 0) regions.Add(i);
            if (regions.Count == 0) return;
            int region = regions[random.Next(regions.Count)];
            ComplaintRecord odd = new ComplaintRecord { Region = region, Reason = ComplaintRules.OddReason, Variant = random.Next(ComplaintRules.OddRequests.Length), Open = true, Since = now.Ticks, Deadline = (now.Ticks + GameClock.Hours(72)) };
            records[region * ComplaintRules.Slots + ComplaintRules.OddReason] = odd;
            // Quoted so the relayed citizen wording is not read as the office's own voice.
            News(odd, city, L.T("다음 내용의 민원이 접수되었습니다. “") + ComplaintRules.OddRequests[odd.Variant] + "”");
        }
        internal static bool IsOpen(int region, int reason)
        {
            ComplaintRecord c;
            return records.TryGetValue(region * ComplaintRules.Slots + reason, out c) && c.Open;
        }
        internal static bool Ready(int origin, long now)
        {
            foreach (ComplaintRecord c in records.Values)
                if ((origin < 0 || c.Region == origin) && ComplaintRules.Escalates(c, now)) return true;
            return false;
        }
        internal static RallyNotice PrepareNotice(int origin, CityView city, DateTime now)
        {
            ComplaintRecord selected = null;
            foreach (ComplaintRecord c in records.Values)
                if ((origin < 0 || c.Region == origin) && ComplaintRules.Escalates(c, now.Ticks) && (selected == null || c.Since < selected.Since)) selected = c;
            if (selected == null) return null;
            string road = L.T("주요 피해 도로 미확인"); RoadResidents[] top;
            if (worst.TryGetValue(selected.Region * ComplaintRules.Slots + selected.Reason, out top) && LiveRoad(top[0])) road = Singleton<NetManager>.instance.GetSegmentName(top[0].Road);
            ComplaintHotspot location;
            if(Local(selected.Reason) && hotspots.TryGetValue(selected.Region*ComplaintRules.Slots+selected.Reason,out location) && location.Live())road=location.Label();
            return new RallyNotice { Origin = origin, Reason = selected.Reason, Since = selected.Since, Due = (now.Ticks + GameClock.Hours(CivicRecords.Patience(origin))),
                Area = origin < 0 ? city.CityName : city.Regions[origin].Name, Road = string.IsNullOrEmpty(road) ? L.T("이름 없는 도로") : road };
        }
        internal static bool OfficeExpired(int origin, long now)
        {
            bool any = false;
            foreach (ComplaintRecord c in records.Values)
                if (c.Reason != ComplaintRules.OddReason && (origin < 0 || c.Region == origin) && c.Open)
                {
                    any = true;
                    if (c.OfficeUntil == 0) c.OfficeUntil = now + GameClock.Hours(Difficulty.OfficeHours * (ComplaintRules.Labor(c.Reason) ? 3 : 1));
                    if (now >= c.OfficeUntil) return true;
                }
            return !any;
        }
        internal static void End(int origin, long now)
        {
            foreach (ComplaintRecord c in records.Values)
                if (c.Reason != ComplaintRules.OddReason && (origin < 0 || c.Region == origin))
                { c.QuietUntil = now + GameClock.HourTicks * 24; c.OfficeUntil = 0; }
        }
        internal static void Decide(int key, long stamp, bool accept, bool agree, CityView city, DateTime now)
        {
            ComplaintRecord c;
            if (key >= CivicStanding.OppositionKeyBase) { CivicStanding.Decide(key, stamp, accept, agree, city, now); return; }
            if (agree) return;
            if (preserved != null || !records.TryGetValue(key, out c)) return;
            long previousDeadline=c.Deadline;
            if(!ComplaintRules.Decide(c, stamp, accept, now.Ticks)) return;
            if(accept) ComplaintRecurrence.AdjustPromise(c,previousDeadline);
            if(!accept && c.Reason!=ComplaintRules.OddReason && city.Regions[c.Region]!=null && city.Regions[c.Region].OfficeId!=0) CivicRecords.ChangeTrust(c.Region,-2);
            string message = accept ? (c.Reason == ComplaintRules.OddReason ? L.T("검토를 접수했습니다. 6게임 시간 뒤 답변합니다.") : L.T("개선 요청을 수락했습니다. 대응 기한을 한 차례 연장합니다.")) :
                c.Reason == ComplaintRules.OddReason ? L.T("기타 요구를 거절하고 종결했습니다.") : L.T("요청을 거절했습니다. 실제 생활 문제와 행복도 감점은 남아 있습니다.");
            CivicChirper.PostComplaint((city.Regions[c.Region] == null ? L.T("지역") : city.Regions[c.Region].Name) + " · " + message, city, c.Region);
        }
        private static bool LiveRoad(RoadResidents road)
        {
            NetSegment[] roads = Singleton<NetManager>.instance.m_segments.m_buffer;
            return road != null && road.Road != 0 && road.Road < roads.Length && roads[road.Road].m_buildIndex == road.BuildIndex &&
                (roads[road.Road].m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) == NetSegment.Flags.Created;
        }
        internal static ComplaintView[] Snapshot(CityView city, DateTime now)
        {
            List<ComplaintView> list = new List<ComplaintView>();
            foreach (KeyValuePair<int, ComplaintRecord> pair in records)
            {
                ComplaintRecord c = pair.Value;
                RegionView area = city.Regions[c.Region];
                if (!c.Open || area == null) continue;
                if (c.Reason == ComplaintRules.OddReason)
                {
                    list.Add(new ComplaintView { Key = pair.Key, Stamp = c.Since, Region = c.Region, Odd = true,
                        Title = area.Name + L.T(" · 기타 요구"), Detail = c.Promised ? L.T("검토 접수 · 답변 준비 중") : L.T("황당 민원 · 답변 대기"),
                        Stage = area.OfficeTransferred ? area.TransferStage : (area.OfficeId != 0 ? area.Name + L.T(" 구청") : L.T("시청")) + L.T(" 담당"),
                        Road = OddText(c), Hours = Math.Max(0, (c.Deadline - now.Ticks) / (double)GameClock.HourTicks),
                        CanPromise = !c.Promised && now.Ticks < c.Deadline, CanReject = !c.Promised });
                    continue;
                }
                ComplaintView v = new ComplaintView { Key = pair.Key, Region = c.Region,
                    Stamp = c.Since, CanReject = !c.Promised,
                    Title = area.Name + " · " + Happiness.Reasons[c.Reason] + ComplaintRecurrence.Badge(c),
                    Detail = L.T("구 전체 기준 · 피해 ") + Affected(area,c.Region,c.Reason).ToString("N0") + L.T("명 · 심각 ") + Severe(area,c.Region,c.Reason).ToString("N0") + L.T("명"),
                    Stage = area.OfficeTransferred ? area.TransferStage : area.OfficeId != 0 ? area.Name + L.T(" 구청 담당") : L.T("시청 직접 담당"),
                    Hours = Math.Max(0, (c.Deadline - now.Ticks) / (double)GameClock.HourTicks),
                    CanPromise = !c.Promised && now.Ticks < c.Deadline,
                    Road = L.T("문제 도로: 확인 가능한 주거지 도로 없음") };
                string recurrence=ComplaintRecurrence.Detail(c);
                if(recurrence.Length>0) v.Detail += "\n"+recurrence;
                RoadResidents[] top;
                if (worst.TryGetValue(pair.Key, out top))
                {
                    string lines = "";
                    foreach (RoadResidents r in top)
                    {
                        if (!LiveRoad(r)) continue;
                        string name = Singleton<NetManager>.instance.GetSegmentName(r.Road);
                        if (string.IsNullOrEmpty(name)) name = L.T("이름 없는 도로 #") + r.Road;
                        if (!v.HasRoad) { v.Position = UnrestRoads.Curve(Singleton<NetManager>.instance.m_segments.m_buffer[r.Road]).Position(.5f); v.HasRoad = true; }
                        double percent = 100.0 * r.Affected[c.Reason] / r.Residents;
                        lines += (lines.Length == 0 ? "" : "\n") + name + " · " + r.Affected[c.Reason].ToString("N0") + L.T("명 (") + (percent < .1 ? L.T("0.1% 미만") : percent.ToString("0.0") + "%") + ")";
                    }
                    if (lines.Length > 0) v.Road = L.T("주요 피해 도로\n") + lines;
                }
                if(c.Reason==2 || c.Reason==17){
                    v.HasRoad=false;
                    v.LocationLabel=L.T("문제 장소 보기");
                    v.Road=L.T("최근 집계에서 실제 혼잡 위치를 확인하지 못했습니다.");
                    ComplaintHotspot spot;
                    if(hotspots.TryGetValue(pair.Key,out spot) && spot.Live()){
                        v.HasRoad=true;v.Position=spot.Position;v.Hotspot=spot;
                        v.Road=spot.Label()+L.T("\n관할 현장 피해 시민 ")+spot.Affected.ToString("N0")+L.T("명")+(c.Reason==17?L.T(" · 대기 지연"):L.T(" · 교통 밀도 ")+spot.Density);
                    }
                }
                if(!Local(c.Reason)) {
                    v.HasRoad=false;v.LocationLabel=L.T("문제 장소 보기");
                    if(c.Reason>=19 && c.Reason<=24) {
                        ComplaintSite site;Vector3 sitePosition;
                        if(sites.TryGetValue(pair.Key,out site) && site.Position(out sitePosition)) {
                            v.Site=site;v.HasRoad=true;v.Position=sitePosition;
                            v.Detail += L.T("\n문제가 확인된 사업장으로 이동합니다.");
                        }
                        else v.Detail += L.T("\n문제가 확인된 사업장 위치를 찾을 수 없습니다.");
                    }
                    else {
                        ComplaintHome representative;Vector3 homePosition;
                        if(homes.TryGetValue(pair.Key,out representative) && representative.Position(out homePosition)) {
                            v.Home=representative;v.HasRoad=true;v.Position=homePosition;
                            v.Detail += L.T("\n대표 피해 시민 #") + representative.Citizen + L.T(" · 거주지로 이동");
                        }
                        else v.Detail += L.T("\n대표 피해 시민의 현재 거주지를 확인할 수 없습니다.");
                    }
                }
                double rate = ComplaintRules.Rate(Affected(area,c.Region,c.Reason), Severe(area,c.Region,c.Reason), Population(area,c.Region,c.Reason)) * 100;
                if (c.Reason == 11) v.Detail += L.T("\n의료 민원 판정: 게임 의료 서비스 범위 부족과 건강 문제가 함께 확인되었습니다.");
                if (c.Reason == 4 || c.Reason == 5 || c.Reason == 6 || c.Reason == 11 || c.Reason == 12 || c.Reason == 14)
                    v.Detail += L.T("\n건물 상태 판정: 불량 방향 60%부터 경고, 80%부터 심각입니다. 실제 화재는 항상 심각입니다.");
                if (c.Reason == 10) v.Detail += "\n" + L.T("해당 교육 단계 이수율 40% 이하와 학교 배정 부족을 함께 확인 · 20% 이하는 심각");
                if (c.LaborExtended) v.Detail += L.T("\n고용 피해율 20% 감소 확인 · 추가 대응 시간 지급 완료");
                v.Road += "\n\n" + (Local(c.Reason) ? L.T("현장 관할 기준 환산 피해 ") : L.T("구 전체 환산 피해 ")) + rate.ToString("0.###") + L.T("% · 접수 > ") +
                    (ComplaintRules.OpenRatio(c.Reason)*100).ToString("0.###") + L.T("% · 종결 ≤ ") +
                    (ComplaintRules.CloseRatio(c.Reason)*100).ToString("0.###") + L.T("% (3시간 유지)");
                if (c.ImprovingSince > 0) {
                    v.Improving = true;
                    v.Hours = Math.Max(0, 3 - (now.Ticks - c.ImprovingSince) / (double)GameClock.HourTicks);
                    v.Stage += L.T(" · 개선 확인 중 (3시간 유지 후 종결)");
                }
                else if (now.Ticks >= c.Deadline) v.Stage += L.T(" · 대응 기한 초과");
                else if (c.Promised) v.Stage += L.T(" · 개선 약속 이행 중");
                list.Add(v);
            }
            // Complaints whose resident is still on the way to the office (CivicVisits), listed after filed ones.
            foreach (VisitRecord pending in CivicVisits.Pending())
            {
                ComplaintRecord pc;
                if (!records.TryGetValue(pending.Key, out pc) || pc.Open || pc.Region < 0 || pc.Region >= city.Regions.Length || city.Regions[pc.Region] == null) continue;
                RegionView pa = city.Regions[pc.Region];
                list.Add(new ComplaintView { Key = pending.Key, Region = pc.Region, Stamp = -1, Visitor = pending.Citizen, HasRoad = true,
                    Title = pa.Name + " · " + Happiness.Reasons[pc.Reason] + L.T(" · 방문 접수 중"),
                    Detail = L.T("주민이 담당 행정청으로 이동하고 있습니다. 도착하면 민원이 접수되고 대응 기한이 시작됩니다. 도착하지 못하면 일반 접수로 전환됩니다."),
                    Stage = pa.OfficeTransferred ? pa.TransferStage : pa.OfficeId != 0 ? pa.Name + L.T(" 구청으로 이동 중") : L.T("시청으로 이동 중"),
                    Hours = Math.Max(0, (pending.Started + GameClock.Hours(VisitRules.TimeoutHours) - now.Ticks) / (double)GameClock.HourTicks),
                    Road = L.T("방문 중인 민원은 도착해 접수된 뒤에 수락하거나 종결할 수 있습니다."),
                    LocationLabel = L.T("방문자 따라가기") });
            }
            CivicStanding.Views(city, now, list);
            list.Sort(delegate(ComplaintView a, ComplaintView b) { int visiting = (a.Visitor != 0).CompareTo(b.Visitor != 0); if (visiting != 0) return visiting; int kinds = ((int)a.Kind).CompareTo((int)b.Kind); if (kinds != 0) return kinds; int kind = a.Odd.CompareTo(b.Odd); if (kind != 0) return kind; int order = a.Hours.CompareTo(b.Hours); return order != 0 ? order : a.Key.CompareTo(b.Key); });
            return list.ToArray();
        }
        private static void News(ComplaintRecord c, CityView city, string text)
        {
            if (newsBudget-- <= 0) { groupedNews++; return; }
            string region = city.Regions[c.Region] == null ? L.T("변경된 지구") : city.Regions[c.Region].Name;
            string road = "";
            RoadResidents[] top;
            if (worst.TryGetValue(c.Region * ComplaintRules.Slots + c.Reason, out top) && LiveRoad(top[0]))
                road = " · " + Singleton<NetManager>.instance.GetSegmentName(top[0].Road);
            ComplaintHotspot spot;
            if((c.Reason==2 || c.Reason==17) && hotspots.TryGetValue(c.Region*ComplaintRules.Slots+c.Reason,out spot) && spot.Live())road=" · "+spot.Label();
            CivicChirper.PostComplaint(region + road + " · " + (c.Reason == ComplaintRules.OddReason ? L.T("기타 요구") : Happiness.Reasons[c.Reason]) + ": " + text, city, c.Region);
        }
    }
}
