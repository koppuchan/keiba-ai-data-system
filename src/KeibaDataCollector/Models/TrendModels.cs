using System;
using System.Collections.Generic;

namespace KeibaDataCollector.Models
{
    /// <summary>仕様書§10「本日の傾向」の3段階。</summary>
    public enum TrendStage
    {
        /// <summary>朝: 過去データ＋当日確定情報（事前想定傾向）。</summary>
        Morning,

        /// <summary>開催中: 当日結果を逐次追加（現時点の傾向）。</summary>
        Live,

        /// <summary>終了後: 全当日結果（本日の結果分析）。</summary>
        Final,
    }

    /// <summary>当日・当該開催場の天候・馬場状態。朝/開催中/終了後いずれの段階でも
    /// 「当日確定情報」として、その時点でソースから読める最新のRAレコードの値を使う
    /// （馬場状態は開催中に悪化/回復することがあるため、段階が進むほど新しい値になる）。</summary>
    public class WeatherTrackInfo
    {
        public string WeatherCode { get; set; }        // TenkoCD
        public string TurfConditionCode { get; set; }   // SibaBabaCD
        public string DirtConditionCode { get; set; }   // DirtBabaCD
    }

    /// <summary>脚質傾向: 複勝圏内(1〜3着)の馬が、それ以外の馬より前目のコーナー通過順位
    /// （最も早いコーナーでの通過順位を0〜1正規化した値。0=先頭）を取っているかどうか。
    /// FactorScoringService.ComputePaceScoreと同じ考え方をコース単位ではなく開催場全体に広げたもの。</summary>
    public class PaceTendency
    {
        public const int MinSample = 20;

        public int SampleCount { get; set; }
        public double? PlacedAvgEarlyPositionRatio { get; set; }
        public double? RestAvgEarlyPositionRatio { get; set; }

        /// <summary>true=先行有利、false=差し有利、null=サンプル不足で判定しない。</summary>
        public bool? FrontRunnerFavored { get; set; }

        public bool HasEnoughSample => SampleCount >= MinSample;
    }

    public class WakuStat
    {
        public int SampleCount { get; set; }
        public double? PlaceRate { get; set; } // 連対率(1-2着)
    }

    /// <summary>枠傾向: 枠番(1〜8)ごとの連対率。開催場全体（全距離・全馬場種別を合算）の粗い傾向。</summary>
    public class PostPositionTendency
    {
        public const int MinSample = 20;
        public Dictionary<int, WakuStat> ByWaku { get; set; } = new Dictionary<int, WakuStat>();
    }

    /// <summary>上がり傾向: 直近レース群の後3ハロンタイム平均（馬場種別ごと）。
    /// 短いほど上がりが速い＝瞬発力勝負の傾向が強いことを示す。</summary>
    public class AgariTendency
    {
        public const int MinSample = 20;
        public int SampleCount { get; set; }
        public double? AverageAgari3F { get; set; }
    }

    /// <summary>通過順傾向: 最終コーナーを先頭で通過した馬がそのまま勝ち切る率
    /// （＝逃げ・先行決着の起こりやすさ）。</summary>
    public class PassageTendency
    {
        public const int MinSample = 10; // レース単位のサンプルのため、馬単位の他指標より少なめに設定
        public int SampleCount { get; set; }
        public double? LeaderWinRate { get; set; }
    }

    /// <summary>開催場1つ・1段階分の「本日の傾向」スナップショット。</summary>
    public class VenueTrendSnapshot
    {
        public DateTime RaceDate { get; set; }
        public string TrackCode { get; set; }
        public TrendStage Stage { get; set; }
        public DateTime ComputedAtUtc { get; set; }

        public WeatherTrackInfo WeatherTrack { get; set; }
        public PaceTendency Pace { get; set; }
        public PostPositionTendency PostPosition { get; set; }
        public AgariTendency Agari { get; set; }
        public PassageTendency Passage { get; set; }

        /// <summary>この段階の集計に使ったレース数（Morning=直近実績のあるレース数ではなく
        /// 過去データの母集団規模の目安、Live/Final=本日の確定済みレース数）。</summary>
        public int RacesConsidered { get; set; }
    }
}
