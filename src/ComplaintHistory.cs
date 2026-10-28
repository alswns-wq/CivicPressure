using System;
using System.IO;
using System.Collections.Generic;

namespace CivicPressure
{
    internal sealed class ComplaintOutcome
    {
        internal int Region, Reason, Remaining;
        internal long Detected, Ended;
        internal bool Promised;
        internal string Area, Office;
    }
    internal static class ComplaintHistory
    {
        internal const string Key = "CivicPressure.ComplaintHistory";
        private static readonly List<ComplaintOutcome> entries = new List<ComplaintOutcome>();
        private static byte[] preserved;
        internal static string Error = "";
        internal static long Revision;
        internal static void Reset() { Revision++; entries.Clear(); preserved = null; Error = ""; }
        internal static void Resolved(int region, int reason, long detected, long ended, bool promised, RegionView area, string cityHall)
        {
            if (preserved != null || area == null || region < 0 || region > 255 || reason < 0 || reason >= ComplaintRules.ReasonCount || detected <= 0 || ended < detected) return;
            Revision++;
            entries.Insert(0, new ComplaintOutcome { Region = region, Reason = reason, Detected = detected, Ended = ended,
                Promised = promised, Area = Short(area.Name), Office = Short(area.OfficeTransferred ? area.ServiceOffice : area.OfficeId != 0 ? area.Office : cityHall), Remaining = area.Affected[reason] });
            if (entries.Count > 100) entries.RemoveAt(100);
        }
        internal static void SeedRecurrence() { foreach(ComplaintOutcome e in entries) ComplaintRecurrence.Seed(e.Region,e.Reason,e.Ended); }
        private static string Short(string s) { return string.IsNullOrEmpty(s) ? L.T("미지정") : s.Length > 160 ? s.Substring(0, 160) : s; }
        internal static string[] Pages(int region)
        {
            if (preserved != null) return new string[] { Error };
            List<string> pages = new List<string>();
            foreach (ComplaintOutcome e in entries)
            {
                if (region >= 0 && e.Region != region) continue;
                pages.Add(e.Area + " · " + Happiness.Reasons[e.Reason] + "\n\n" +
                    (e.Remaining == 0 ? L.T("완전 해결") : L.T("개선 확인 후 종결")) + (e.Promised ? L.T(" · 개선 약속 이행") : "") +
                    L.T("\n\n종결 당시 담당: ") + e.Office + L.T("\n종결: ") + new DateTime(e.Ended).ToString("yyyy-MM-dd HH:mm") +
                    L.T("\n\n문제 감지부터 종결까지: ") + ((e.Ended - e.Detected) / (double)GameClock.HourTicks).ToString("0.0") + L.T(" 게임 시간") +
                    L.T("\n종결 당시 잔여 피해: ") + e.Remaining.ToString("N0") + L.T("명") +
                    (e.Remaining > 0 ? L.T("\n낮은 피해 비율이 3시간 유지되어 종결했습니다.") : ""));
            }
            return pages.ToArray();
        }
        internal static byte[] Save()
        {
            if (preserved != null) return (byte[])preserved.Clone();
            using (MemoryStream s = new MemoryStream()) using (BinaryWriter w = new BinaryWriter(s))
            {
                w.Write(0x43504831); w.Write(1); w.Write(entries.Count);
                foreach (ComplaintOutcome e in entries) { w.Write(e.Region); w.Write(e.Reason); w.Write(e.Remaining); w.Write(e.Detected); w.Write(e.Ended); w.Write(e.Promised); w.Write(e.Area); w.Write(e.Office); }
                return s.ToArray();
            }
        }
        internal static void Load(byte[] bytes)
        {
            Reset(); if (bytes == null || bytes.Length == 0) return;
            try
            {
                if (bytes.Length > 150000) throw new InvalidDataException();
                using (MemoryStream s = new MemoryStream(bytes)) using (BinaryReader r = new BinaryReader(s))
                {
                    if (r.ReadInt32() != 0x43504831 || r.ReadInt32() != 1) throw new InvalidDataException();
                    int count = r.ReadInt32(); if (count < 0 || count > 100) throw new InvalidDataException();
                    for (int i = 0; i < count; i++)
                    {
                        ComplaintOutcome e = new ComplaintOutcome { Region = r.ReadInt32(), Reason = r.ReadInt32(), Remaining = r.ReadInt32(), Detected = r.ReadInt64(), Ended = r.ReadInt64(), Promised = r.ReadBoolean(), Area = r.ReadString(), Office = r.ReadString() };
                        if (e.Region < 0 || e.Region > 255 || e.Reason < 0 || e.Reason >= ComplaintRules.ReasonCount || e.Remaining < 0 || e.Detected <= 0 || e.Ended < e.Detected || e.Ended > DateTime.MaxValue.Ticks || e.Area.Length > 160 || e.Office.Length > 160) throw new InvalidDataException();
                        entries.Add(e);
                    }
                    if (s.Position != s.Length) throw new InvalidDataException();
                }
            }
            catch (Exception) { entries.Clear(); preserved = (byte[])bytes.Clone(); Error = L.T("민원 이력을 읽을 수 없어 원본을 보존했습니다. 새 이력 기록은 중지됩니다."); }
        }
    }
}
