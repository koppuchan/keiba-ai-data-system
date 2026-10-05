using System;
using System.Collections.Generic;

namespace KeibaDataCollector.Models
{
    /// <summary>公開済みの予測1件と、その確定結果。同じ予測が20分おきに再生成されるため、
    /// 集計では（レース・馬・カテゴリ・モデル）ごとに最初に公開した1件だけを使う。</summary>
    public class VerifiedPick
    {
        public string RaceDate { get; set; }
        public string TrackCode { get; set; }
        public int RaceNumber { get; set; }
        public int Umaban { get; set; }
        public string Category { get; set; }
        public string ModelVersion { get; set; }
        public double? AiIndex { get; set; }
        public bool HitTop3 { get; set; }
        public bool HitWin { get; set; }
        public string TrackSurfaceCode { get; set; }
    }

    public class HitStat
    {
        public int Count { get; set; }
        public int Top3 { get; set; }
        public int Wins { get; set; }

        public void Add(VerifiedPick p)
        {
            Count++;
            if (p.HitTop3) Top3++;
            if (p.HitWin) Wins++;
        }
    }

    public class GroupStat : HitStat
    {
        public string Key { get; set; }
        public string Label { get; set; }
    }

    public class BandStat : HitStat
    {
        public int Low { get; set; }
        public int High { get; set; }
    }

    /// <summary>1つのモデルバージョンの検証結果。旧モデルと新モデルを混ぜて集計しないよう、
    /// モデルごとに独立して持つ。</summary>
    public class ModelVerification
    {
        public string ModelVersion { get; set; }
        public int Races { get; set; }
        public int Predictions { get; set; }
        public string PeriodFrom { get; set; }
        public string PeriodTo { get; set; }

        public List<GroupStat> Categories { get; set; } = new List<GroupStat>();
        public HitStat Top5 { get; set; } = new HitStat();
        public HitStat Top1 { get; set; } = new HitStat();
        public List<BandStat> Bands { get; set; } = new List<BandStat>();
        public List<GroupStat> ByLeague { get; set; } = new List<GroupStat>();
        public List<GroupStat> ByVenue { get; set; } = new List<GroupStat>();
        public List<GroupStat> BySurface { get; set; } = new List<GroupStat>();
    }

    public class VerificationReport
    {
        public DateTime GeneratedAtUtc { get; set; }
        public List<ModelVerification> Models { get; set; } = new List<ModelVerification>();
    }
}
