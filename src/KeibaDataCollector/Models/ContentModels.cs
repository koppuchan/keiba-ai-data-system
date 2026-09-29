using System;
using System.Collections.Generic;

namespace KeibaDataCollector.Models
{
    /// <summary>仕様書§11のコンテンツ種別。</summary>
    public enum PickCategory
    {
        /// <summary>狙い馬: 指数上位＋データ充足＋展開/馬場・コース適性等が基準以上。</summary>
        Nerai,

        /// <summary>穴馬: 指数・期待値等と人気/オッズに一定の乖離がある馬。</summary>
        Ana,

        /// <summary>危険な人気馬: 人気上位かつ一部評価に注意点がある馬。</summary>
        Kiken,
    }

    /// <summary>ContentGeneratorへの入力1頭分。当日のAI指数（ScoresStore）と、
    /// 生成時点で改めて読み直した当日の出走状態（取消・オッズ・人気）を突き合わせたもの。</summary>
    public class PickCandidate
    {
        public int RaceNumber { get; set; }
        public int Umaban { get; set; }
        public string KettoNum { get; set; }

        public double? AiIndex { get; set; }
        public double DataCompleteness { get; set; }
        public FactorScores Factors { get; set; }

        /// <summary>生成時点で改めて読み直した最新の取消・除外状態。ScoresStoreの
        /// is_scratchedがscoreコマンド実行時点のスナップショットのままなのに対し、
        /// こちらはContentGeneratorが都度読み直す「今」の状態（仕様書§11
        /// 「取消・騎手変更・馬場変更が未反映なら公開停止または再計算」に対応）。</summary>
        public bool IsCurrentlyScratched { get; set; }

        public int? Ninki { get; set; }
        public double? TanshoOdds { get; set; }
    }

    /// <summary>生成された1件のコンテンツ。文章は構造化データの値のみを埋め込んだテンプレートで、
    /// AIに数値や馬番を自由生成させない（仕様書§14）。Reasonsは検証用の根拠（Validator/Issue #6が
    /// 元データと突き合わせる際に使う想定）。</summary>
    public class GeneratedPick
    {
        public PickCategory Category { get; set; }
        public RaceKey Race { get; set; }
        public int Umaban { get; set; }
        public string KettoNum { get; set; }
        public string Text { get; set; }
        public List<string> Reasons { get; set; } = new List<string>();

        /// <summary>生成時点で参照した実データ。Validator（仕様書§14・Issue #6）が
        /// 公開直前にDBの「今」の値と突き合わせて不一致を検知するための基準値。
        /// Reasonsは人間可読なログ用の文字列のため、機械的な再照合にはこちらを使う。</summary>
        public double? AiIndexAtGeneration { get; set; }
        public int? NinkiAtGeneration { get; set; }
        public double? TanshoOddsAtGeneration { get; set; }
    }
}
