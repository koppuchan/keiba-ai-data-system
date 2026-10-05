using System;

namespace KeibaDataCollector.Models
{
    /// <summary>verificationテーブルの1行。predictions 1件に対する結果照合。</summary>
    public class VerificationRecord
    {
        public string PredictionId { get; set; }
        public DateTime RaceDate { get; set; }
        public string TrackCode { get; set; }
        public int RaceNumber { get; set; }
        public int Umaban { get; set; }
        public string Category { get; set; }
        public string ModelVersion { get; set; }
        public int Chakujun { get; set; }
        public bool HitTop3 { get; set; }
        public bool HitWin { get; set; }
        public DateTime VerifiedAtUtc { get; set; }

        /// <summary>レースのトラックコード（芝/ダート等の判別用）。サイトの芝・ダート別集計に使う。</summary>
        public string TrackSurfaceCode { get; set; }
    }

    /// <summary>仕様書§18「指数帯別成績をmodel_versionごとに分離」。AI指数を10点刻みの帯に分け、
    /// model_version・カテゴリごとに母数付きで的中率を集計した1行。</summary>
    public class IndexBandStat
    {
        public string ModelVersion { get; set; }
        public string Category { get; set; }
        public int BandLow { get; set; }   // 例: 70
        public int BandHigh { get; set; }  // 例: 80（[70,80)の意味）
        public int SampleCount { get; set; }
        public int Top3Count { get; set; }
        public int WinCount { get; set; }

        public double Top3Rate => SampleCount > 0 ? (double)Top3Count / SampleCount : 0;
        public double WinRate => SampleCount > 0 ? (double)WinCount / SampleCount : 0;
    }

    /// <summary>VerificationService.VerifyVenue 1回分の実行結果。</summary>
    public class VerificationRunSummary
    {
        public int TotalPredictions { get; set; }
        public int Verified { get; set; }
        public int StillPending { get; set; }
    }
}
