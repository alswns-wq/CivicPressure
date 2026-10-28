using System;
using System.IO;
using System.Collections.Generic;
namespace CivicPressure
{
    internal sealed class RecurrenceState
    {
        internal long Solved, Consumed, Stamp;
        internal int Streak, Current;
    }
    internal static class ComplaintRecurrence
    {
        internal static void Rebase(long now, double factor)
        {
            foreach (RecurrenceState s in states.Values) {
                s.Solved = GameClock.Rebase(s.Solved, now, factor);
                s.Consumed = GameClock.Rebase(s.Consumed, now, factor);
                s.Stamp = GameClock.Rebase(s.Stamp, now, factor);
            }
        }
        internal const string Key = "CivicPressure.ComplaintRecurrence";
        private static readonly Dictionary<int,RecurrenceState> states = new Dictionary<int,RecurrenceState>();
        private static byte[] preserved;
        internal static string Error="";
        internal static void Reset(){states.Clear();preserved=null;Error="";}
        private static int Identity(int region,int reason){return region*ComplaintRules.ReasonCount+reason;}
        private static bool Valid(int region,int reason){return region>=0 && region<256 && reason>=0 && reason<ComplaintRules.ReasonCount;}
        internal static void Seed(int region,int reason,long ended)
        {
            if(preserved!=null || !Valid(region,reason) || ended<=0)return;
            int key=Identity(region,reason);RecurrenceState s;
            if(!states.TryGetValue(key,out s)){s=new RecurrenceState();states.Add(key,s);}
            if(ended>s.Solved)s.Solved=ended;
        }
        internal static void Maintain(CityView city)
        {
            if(preserved!=null)return;
            List<int> remove=new List<int>();
            foreach(int key in states.Keys)if(city.Regions[key/ComplaintRules.ReasonCount]==null)remove.Add(key);
            foreach(int key in remove)states.Remove(key);
        }
        internal static void Solved(int region,int reason,long ended)
        {
            Seed(region,reason,ended);
        }
        internal static int Opened(ComplaintRecord c,long now)
        {
            if(preserved!=null || !c.Open || !Valid(c.Region,c.Reason))return 0;
            int key=Identity(c.Region,c.Reason);RecurrenceState s;
            if(!states.TryGetValue(key,out s)){s=new RecurrenceState();states.Add(key,s);}
            if(s.Stamp==c.Since)return 0;
            s.Stamp=c.Since;
            bool repeat=s.Solved>0 && s.Solved>s.Consumed && now>=s.Solved && now-s.Solved<=7*GameClock.DayTicks;
            s.Current=repeat ? Math.Min(99,s.Streak+1) : 0;s.Streak=s.Current;
            s.Consumed=s.Solved;
            if(s.Solved>now){s.Solved=0;s.Consumed=0;}
            if(s.Current==0)return 0;
            c.Deadline=now+(long)((c.Deadline-now)*Factor(c.Reason,s.Current));
            return Math.Min(3,s.Current)*(ComplaintRules.Slow(c.Reason)?1:2);
        }
        private static RecurrenceState Active(ComplaintRecord c)
        {
            RecurrenceState s;
            return preserved==null && c.Open && Valid(c.Region,c.Reason) && states.TryGetValue(Identity(c.Region,c.Reason),out s) && s.Stamp==c.Since ? s : null;
        }
        internal static double Factor(int reason,int count){return 1-Math.Min(3,Math.Max(0,count))*(ComplaintRules.Slow(reason)?.05:.1);}
        internal static void AdjustPromise(ComplaintRecord c,long previousDeadline)
        {
            RecurrenceState s=Active(c);
            if(s!=null && s.Current>0 && c.Deadline>previousDeadline)c.Deadline=previousDeadline+(long)((c.Deadline-previousDeadline)*Factor(c.Reason,s.Current));
        }
        internal static string Badge(ComplaintRecord c)
        {
            RecurrenceState s=Active(c);return s==null || s.Current==0 ? "" : L.T(" [재발 ")+s.Current+L.T("회]");
        }
        internal static string Detail(ComplaintRecord c)
        {
            RecurrenceState s=Active(c);
            return s==null || s.Current==0 ? "" : L.T("이전 해결 ")+new DateTime(s.Consumed).ToString("MM-dd HH:mm")+L.T(" · 대응 기한 ")+((1-Factor(c.Reason,s.Current))*100).ToString("0")+L.T("% 단축");
        }
        internal static byte[] Save()
        {
            if(preserved!=null)return (byte[])preserved.Clone();
            using(MemoryStream stream=new MemoryStream())using(BinaryWriter w=new BinaryWriter(stream)){
                w.Write(0x43505243);w.Write(2);w.Write(states.Count);
                foreach(KeyValuePair<int,RecurrenceState> pair in states){RecurrenceState s=pair.Value;w.Write(pair.Key);w.Write(s.Solved);w.Write(s.Consumed);w.Write(s.Stamp);w.Write(s.Streak);w.Write(s.Current);}
                return stream.ToArray();
            }
        }
        internal static void Load(byte[] bytes)
        {
            Reset();if(bytes==null || bytes.Length==0)return;
            try{
                if(bytes.Length>300000)throw new InvalidDataException();
                using(BinaryReader r=new BinaryReader(new MemoryStream(bytes))){
                    if(r.ReadInt32()!=0x43505243)throw new InvalidDataException(); int version=r.ReadInt32(); if(version<1 || version>2)throw new InvalidDataException(); int width=version==1?18:ComplaintRules.ReasonCount;
                    int count=r.ReadInt32();if(count<0 || count>256*width || bytes.Length!=12+count*36)throw new InvalidDataException();
                    for(int i=0;i<count;i++){
                        int key=r.ReadInt32(); if(key<0 || key>=256*width)throw new InvalidDataException(); key=(key/width)*ComplaintRules.ReasonCount+key%width; RecurrenceState s=new RecurrenceState{Solved=r.ReadInt64(),Consumed=r.ReadInt64(),Stamp=r.ReadInt64(),Streak=r.ReadInt32(),Current=r.ReadInt32()};
                        if(key<0 || key>=256*ComplaintRules.ReasonCount || states.ContainsKey(key) || s.Streak<0 || s.Streak>99 || s.Current!=s.Streak || s.Consumed>s.Solved || (s.Current>0 && s.Consumed==0))throw new InvalidDataException();
                        foreach(long t in new long[]{s.Solved,s.Consumed,s.Stamp})if(t<0 || t>DateTime.MaxValue.Ticks)throw new InvalidDataException();
                        states.Add(key,s);
                    }
                }
            }catch(Exception){states.Clear();preserved=(byte[])bytes.Clone();Error=L.T("재발 민원 기록을 읽을 수 없어 원본을 보존했습니다. 재발 불이익은 중지됩니다.");}
        }
    }
}
