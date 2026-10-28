using System;
using System.IO;
using System.Collections.Generic;
namespace CivicPressure
{
    internal sealed class TrendPoint
    {
        internal long Time;
        internal double Score;
        internal double[] Penalties = new double[Happiness.Weights.Length+1];
    }
    internal static class HappinessTrend
    {
        internal const string Key = "CivicPressure.HappinessTrend";
        private static readonly List<TrendPoint>[] series = NewSeries();
        private static List<TrendPoint>[] NewSeries() { List<TrendPoint>[] a=new List<TrendPoint>[257];for(int i=0;i<a.Length;i++)a[i]=new List<TrendPoint>();return a; }
        private static byte[] preserved;
        private static long census;
        internal static string Error="";
        internal static long Revision;
        internal static void ClockChanged() { if (preserved == null) Reset(); }
        internal static void Reset(){Revision++;foreach(List<TrendPoint> s in series)s.Clear();preserved=null;census=0;Error="";}
        internal static void Capture(CityView city,long now)
        {
            if(preserved!=null || city.All==null || city.CensusUpdatedUtc.Ticks==census)return;
            Revision++; census=city.CensusUpdatedUtc.Ticks;
            double[] total=new double[Happiness.Weights.Length+1];int districts=0;
            for(int i=0;i<256;i++){
                RegionView r=city.Regions[i];
                if(r==null || double.IsNaN(r.Score)){series[i+1].Clear();continue;}
                TrendPoint p=Point(r,now);Add(series[i+1],p);
                if(i>0){districts++;for(int j=0;j<Happiness.Weights.Length+1;j++)total[j]+=p.Penalties[j];}
            }
            if(double.IsNaN(city.CityScore)){series[0].Clear();return;}
            TrendPoint all=Point(city.All,now);all.Score=city.CityScore;
            if(districts>0)for(int j=0;j<Happiness.Weights.Length+1;j++)all.Penalties[j]=total[j]/districts;
            Add(series[0],all);
        }
        private static TrendPoint Point(RegionView r,long now)
        {
            TrendPoint p=new TrendPoint{Time=now,Score=r.Score};
            for(int i=0;i<Happiness.Weights.Length;i++)p.Penalties[i]=Happiness.Penalty(i,r.Affected[i],r.Severe[i],r.Residents);
            p.Penalties[Happiness.Weights.Length]=Happiness.AbandonmentPenalty(r.Abandoned,r.Buildings);return p;
        }
        private static void Add(List<TrendPoint> s,TrendPoint p)
        {
            if(s.Count>0 && p.Time<s[s.Count-1].Time)s.Clear();
            while(s.Count>0 && p.Time-s[0].Time>GameClock.DayTicks)s.RemoveAt(0);
            if(s.Count>0 && s[s.Count-1].Time/GameClock.HourTicks==p.Time/GameClock.HourTicks)s[s.Count-1]=p;
            else s.Add(p);
            while(s.Count>25)s.RemoveAt(0);
        }
        internal static TrendPoint[] Points(int region)
        {
            if(region < -1 || region>255)return new TrendPoint[0];
            List<TrendPoint> result=new List<TrendPoint>();
            foreach(TrendPoint p in series[region+1])result.Add(new TrendPoint{Time=p.Time,Score=p.Score,Penalties=(double[])p.Penalties.Clone()});
            return result.ToArray();
        }
        internal static string Summary(TrendPoint[] p)
        {
            if(p.Length<2)return L.T("기록 수집 중");
            double delta=p[p.Length-1].Score-p[0].Score;
            return delta.ToString("+0.0;-0.0;0.0")+L.T("점 · ")+((p[p.Length-1].Time-p[0].Time)/(double)GameClock.HourTicks).ToString("0.#")+L.T("시간 대비");
        }
        internal static string Details(TrendPoint[] p)
        {
            if(Error.Length>0)return Error;
            if(p.Length<2)return L.T("다음 게임 시간대의 집계가 완료되면 변화가 표시됩니다.");
            double best=0,worst=0;int bi=-1,wi=-1;
            for(int i=0;i<Happiness.Weights.Length+1;i++){double d=p[p.Length-1].Penalties[i]-p[0].Penalties[i];if(d<best){best=d;bi=i;}if(d>worst){worst=d;wi=i;}}
            return L.T("감점 감소: ")+Change(bi,-best)+L.T("\n감점 증가: ")+Change(wi,worst);
        }
        private static string Change(int reason,double d){return reason<0 || d<.05 ? L.T("뚜렷한 변화 없음") : (reason==Happiness.Weights.Length?L.T("버려진 건물"):Happiness.Reasons[reason])+" · "+d.ToString("0.0")+L.T("점");}
        internal static byte[] Save()
        {
            if(preserved!=null)return (byte[])preserved.Clone();
            using(MemoryStream s=new MemoryStream())using(BinaryWriter w=new BinaryWriter(s)){
                w.Write(0x43505431);w.Write(2);
                foreach(List<TrendPoint> list in series){w.Write(list.Count);foreach(TrendPoint p in list){w.Write(p.Time);w.Write(p.Score);foreach(double d in p.Penalties)w.Write(d);}}
                return s.ToArray();
            }
        }
        internal static void Load(byte[] bytes)
        {
            Reset();if(bytes==null || bytes.Length==0)return;
            try{
                if(bytes.Length>1600000)throw new InvalidDataException();
                using(MemoryStream s=new MemoryStream(bytes))using(BinaryReader r=new BinaryReader(s)){
                    if(r.ReadInt32()!=0x43505431)throw new InvalidDataException(); int version=r.ReadInt32(); if(version<1 || version>2)throw new InvalidDataException(); int width=version==1?19:Happiness.Weights.Length+1;
                    foreach(List<TrendPoint> list in series){
                        int n=r.ReadInt32();if(n<0 || n>25)throw new InvalidDataException();
                        for(int i=0;i<n;i++){
                            TrendPoint p=new TrendPoint{Time=r.ReadInt64(),Score=r.ReadDouble()};
                            if(p.Time<=0 || p.Time>DateTime.MaxValue.Ticks || double.IsNaN(p.Score) || p.Score<0 || p.Score>100)throw new InvalidDataException();
                            for(int j=0;j<width;j++){double d=r.ReadDouble();if(double.IsNaN(d)||double.IsInfinity(d)||d<0)throw new InvalidDataException();p.Penalties[version==1 && j==18?Happiness.Weights.Length:j]=d;}
                            if(list.Count>0 && (p.Time<=list[list.Count-1].Time || p.Time-list[0].Time>(long)(TimeSpan.TicksPerDay*GameClock.MaxScale)))throw new InvalidDataException();
                            list.Add(p);
                        }
                    }
                    if(s.Position!=s.Length)throw new InvalidDataException();
                }
            }catch(Exception){Reset();preserved=(byte[])bytes.Clone();Error=L.T("행복도 추이를 읽을 수 없어 원본을 보존했습니다.");}
        }
    }
}
