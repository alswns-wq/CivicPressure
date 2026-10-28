using System;
using System.IO;

namespace CivicPressure
{
    internal static class AdministrationTitle
    {
        internal static string City(string name)
        {
            if (string.IsNullOrEmpty(name)) return L.T("시청");
            name = name.Trim();
            if (L.Current != 0) return name + " " + L.T("시청");
            return name + (name.EndsWith("시") || name.EndsWith("도시") ? L.T(" 시청") : L.T("시 시청"));
        }
    }
    internal struct HallRecord
    {
        internal byte District;
        internal ushort Building;
        internal uint BuildIndex;
    }

    internal static class Happiness
    {
        private static readonly string[] ReasonsSource = {
            "오염·오염수", "소음", "연결도로 혼잡", "전기·수도·하수·난방",
            "쓰레기", "범죄", "건강 악화", "높은 세금",
            "안정된 주거", "일자리", "교육 기회", "의료 서비스", "치안 서비스", "시신 수거 지연", "소방 서비스·화재", "대중교통 접근성", "공원 접근성", "대중교통 대기·혼잡", "도로 연결 단절", "상품 공급 부족", "원자재·연료 부족", "상품 반출 정체", "사업장 인력 부족", "숙련 인력 부족", "상권 고객 부족", "침수 피해"
        };
        internal static string[] Reasons { get { return L.Array(ReasonsSource); } }
        internal static readonly int[] Weights = { 25, 20, 15, 25, 15, 15, 20, 10, 25, 20, 20, 20, 20, 20, 20, 10, 10, 15, 15, 10, 10, 10, 10, 10, 8, 20 };

        internal static double Penalty(int reason, int affected, int residents)
        {
            return Penalty(reason, affected, 0, residents);
        }

        internal static double Weighted(int affected, int severe, int residents)
        {
            int count = Math.Min(Math.Max(0, residents), Math.Max(0, affected));
            return count + 0.5 * Math.Min(count, Math.Max(0, severe));
        }

        internal static double Penalty(int reason, int affected, int severe, int residents)
        {
            int count = Math.Min(Math.Max(0, residents), Math.Max(0, affected));
            if (count == 0) return 0;
            // Job matching takes time. Count the real affected population, without a fixed penalty for each warning.
            if (ComplaintRules.Labor(reason)) return Difficulty.PenaltyScale * Weights[reason] * Weighted(count, severe, residents) / residents;
            // A fixed cost per active grievance makes even small minorities count.
            double basePenalty = (ComplaintRules.Slow(reason) || reason >= 15) ? 2 + .75 * Weights[reason] * Math.Sqrt(count / (double)residents) :
                12 + 2 * Weights[reason] * Math.Sqrt(count / (double)residents);
                        // Small residual populations must not pay the full fixed grievance penalty.
            double residual = Math.Min(1, ComplaintRules.Rate(count, 0, residents) / ComplaintRules.CloseRatio(reason));
            return Difficulty.PenaltyScale * basePenalty * Weighted(count, severe, residents) / count * residual;
        }

        internal static double Score(int residents, int[] affected)
        {
            return Score(residents, affected, new int[Weights.Length], 0, 0);
        }

        internal static double AbandonmentPenalty(int abandoned, int buildings)
        {
            return buildings <= 0 || abandoned <= 0 ? 0 : Difficulty.PenaltyScale * (15 + 60 * Math.Sqrt(Math.Min(buildings, abandoned) / (double)buildings));
        }

        internal static double Score(int residents, int[] affected, int[] severe, int abandoned, int buildings)
        {
            if (residents <= 0 && abandoned <= 0) return double.NaN;
            double value = 100 - AbandonmentPenalty(abandoned, buildings);
            for (int i = 0; i < Weights.Length; i++) value -= Penalty(i, i < affected.Length ? affected[i] : 0, i < severe.Length ? severe[i] : 0, residents);
            return Math.Max(0, Math.Min(100, value));
        }

        internal static string Level(double score)
        {
            if (double.IsNaN(score)) return L.T("집계 대상 없음");
            if (score < 20) return L.T("최악");
            if (score < 40) return L.T("나쁨");
            if (score < 60) return L.T("보통");
            if (score < 80) return L.T("좋음");
            return L.T("만족");
        }

        internal static double CityScore(double[] districtScores, double allResidentsScore)
        {
            double sum = 0;
            int count = 0;
            for (int i = 0; i < districtScores.Length; i++)
            {
                if (double.IsNaN(districtScores[i])) continue;
                sum += districtScores[i];
                count++;
            }
            return count == 0 ? allResidentsScore : sum / count;
        }
    }

    internal static class HallSave
    {
        // Fixed-width records keep corrupt counts/strings from causing unbounded allocations.
        private const int Magic = 0x43504832;
        private const int Version = 1;
        internal const string Key = "CivicPressure.Administration";

        internal static byte[] Encode(HallRecord[] halls)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                int count = 0;
                for (int i = 0; i < halls.Length; i++) if (halls[i].Building != 0) count++;
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(count);
                for (int i = 0; i < halls.Length; i++)
                {
                    HallRecord hall = halls[i];
                    if (hall.Building == 0) continue;
                    writer.Write(hall.District);
                    writer.Write(hall.Building);
                    writer.Write(hall.BuildIndex);
                }
                return stream.ToArray();
            }
        }

        internal static HallRecord[] Decode(byte[] bytes)
        {
            HallRecord[] halls = new HallRecord[256];
            if (bytes == null || bytes.Length == 0) return halls;
            if (bytes.Length < 12 || bytes.Length > 12 + 256 * 7)
                throw new InvalidDataException("Invalid administration save length.");
            using (MemoryStream stream = new MemoryStream(bytes, false))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != Magic) throw new InvalidDataException("Unknown administration save format.");
                if (reader.ReadInt32() != Version) throw new InvalidDataException("Unsupported administration save version.");
                int count = reader.ReadInt32();
                if (count < 0 || count > 256 || bytes.Length != 12 + count * 7)
                    throw new InvalidDataException("Invalid administration record count.");
                for (int i = 0; i < count; i++)
                {
                    HallRecord hall = new HallRecord();
                    hall.District = reader.ReadByte();
                    hall.Building = reader.ReadUInt16();
                    hall.BuildIndex = reader.ReadUInt32();
                    if (hall.Building == 0 || halls[hall.District].Building != 0)
                        throw new InvalidDataException("Invalid or duplicate hall record.");
                    for (int j = 0; j < halls.Length; j++)
                        if (halls[j].Building == hall.Building)
                            throw new InvalidDataException("A building cannot be both city hall and district office.");
                    halls[hall.District] = hall;
                }
            }
            return halls;
        }
    }
}
