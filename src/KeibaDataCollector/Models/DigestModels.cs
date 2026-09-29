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

        /// <summary>LicenseGateの判定結果をそのまま載せる。WordPress側テーマがこれを見て、
        /// 万一データが残っていても表示側でも二重に隠せるようにする
        /// （horse-race-custom-builderのhrc_is_race_visibleと同じ考え方）。</summary>
        public bool LicenseVisible { get; set; }

        public string DigestKey => $"{RaceDate:yyyyMMdd}-{TrackCode}";

        /// <summary>全ブロックが空なら公開する意味が無い（仕様書§13「更新失敗時に空ページ・
        /// 壊れたページを出さない」と同じ考え方をpublish要否の判断にも適用する）。</summary>
        public bool HasAnyContent => AiIndexTop5.Count > 0 || TrendMorning != null || TrendLive != null || TrendFinal != null || Picks.Count > 0;
    }
}
