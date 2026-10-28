namespace CivicPressure
{
    // Owned arrays become read-only after construction, just like a completed city view.
    internal sealed class CensusProjection
    {
        internal readonly CityCensus Source;
        internal readonly int DifficultyLevel;
        internal readonly double[] Scores = new double[256];
        internal readonly int[][] Affected = new int[256][], Severe = new int[256][];
        internal readonly RegionView Total;
        internal CensusProjection(CityCensus source)
        {
            Source=source; DifficultyLevel=Difficulty.Level;
            Total=new RegionView{Name=L.T("도시 전체 집계"),Affected=new int[Happiness.Weights.Length],Severe=new int[Happiness.Weights.Length]};
            for(int i=0;i<256;i++){
                Affected[i]=(int[])source.Affected[i].Clone();Severe[i]=(int[])source.Severe[i].Clone();
                Scores[i]=Happiness.Score(source.Residents[i],Affected[i],Severe[i],source.Abandoned[i],source.Buildings[i]);
                Total.Residents+=source.Residents[i];Total.Buildings+=source.Buildings[i];Total.Abandoned+=source.Abandoned[i];
                Total.WarningBuildings+=source.Warning[i];Total.SevereBuildings+=source.Major[i];
                for(int j=0;j<Total.Affected.Length;j++){Total.Affected[j]+=Affected[i][j];Total.Severe[j]+=Severe[i][j];}
            }
            Total.Unlocated=source.Unlocated; Total.Residents+=source.Unlocated;
            if(source.Unlocated>0)for(int j=0;j<Total.Affected.Length;j++){
                Total.Affected[j]+=source.UnlocatedAffected[j];Total.Severe[j]+=source.UnlocatedSevere[j];
            }
            Total.Score=Happiness.Score(Total.Residents,Total.Affected,Total.Severe,Total.Abandoned,Total.Buildings);
        }
        internal bool Matches(CityCensus source){return object.ReferenceEquals(Source,source) && DifficultyLevel==Difficulty.Level;}
    }
}
