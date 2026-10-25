using System;
using System.Collections.Generic;

namespace KeibaDataCollector.Models
{
    /// <summary>1開催場・1日分の公開コンテンツ一式（AI指数TOP5・本日の傾向・狙い馬/穴馬/危険な人気馬）。
    /// 仕様書§3の完成イメージで1つのまとまりとして扱われている3ブロックを、WordPress側の
    /// 新規カスタム投稿タイプ keiba_digest 1件に対応させる（`race`投稿とは別。`race`は1レース単位、
    /// こちらは1開催場・1日単位のため）。</summary>
    public class DigestPayload
    {
        public DateTime RaceDate { get; set; }
        public string TrackCode { get; set; }

        public List<AiIndexResult> AiIndexTop5 { get; set; } = new List<AiIndexResult>();

        public VenueTrendSnapshot TrendMorning { get; set; }
        public VenueTrendSnapshot TrendLive { get; set; }
        public VenueTrendSnapshot TrendFinal { get; set; }

        public List<GeneratedPick> Picks { get; set; } = new List<GeneratedPick>();

        /// <summary>レースごとの要約（レース名・発走時刻・候補馬の要素別評価）。「今日の狙い目レース診断」が
        /// 発走前のレースを絞り込むための公開データ。無い場合は空（診断側は準備中表示にする）。</summary>
        public List<RaceSummary> Races { get; set; } = new List<RaceSummary>();

        /// <summary>LicenseGateの判定結果をそのまま載せる。WordPress側テーマがこれを見て、
        /// 万一データが残っていても表示側でも二重に隠せるようにする
        /// （horse-race-custom-builderのhrc_is_race_visibleと同じ考え方）。</summary>
        public bool LicenseVisible { get; set; }

        public string DigestKey => $"{RaceDate:yyyyMMdd}-{TrackCode}";

        /// <summary>全ブロックが空なら公開する意味が無い（仕様書§13「更新失敗時に空ページ・
        /// 壊れたページを出さない」と同じ考え方をpublish要否の判断にも適用する）。</summary>
        public bool HasAnyContent => AiIndexTop5.Count > 0 || TrendMorning != null || TrendLive != null || TrendFinal != null || Picks.Count > 0;
    }

    /// <summary>1レース分の公開用要約。AI指数が取得できた候補馬だけを載せる（取得できないレースは
    /// 候補が空になり、診断側で推薦対象から外れる）。</summary>
    public class RaceSummary
    {
        public int RaceNumber { get; set; }
        public string RaceName { get; set; }

        /// <summary>発走時刻 "HH:mm"（不明な場合は空）。</summary>
        public string StartTime { get; set; }

        /// <summary>取消・除外を除いた出走頭数（AI指数が算出できた馬の数）。</summary>
        public int FieldSize { get; set; }

        public List<RaceCandidateInfo> Candidates { get; set; } = new List<RaceCandidateInfo>();
    }

    public class RaceCandidateInfo
    {
        public int Umaban { get; set; }
        public string HorseName { get; set; }
        public double? AiIndex { get; set; }
        public int? Ninki { get; set; }
        public double? TanshoOdds { get; set; }
        public double DataCompleteness { get; set; }
        public FactorScores Factors { get; set; }

        /// <summary>この馬が狙い馬(nerai)・穴馬(ana)・危険な人気馬(kiken)として公開されているか。</summary>
        public List<string> Roles { get; set; } = new List<string>();
    }
}
