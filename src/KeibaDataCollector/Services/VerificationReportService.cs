using System;
using System.Collections.Generic;
using System.Linq;
using KeibaDataCollector.Data;
using KeibaDataCollector.Models;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 仕様書§18「レース後の自動検証」の結果を、サイトに公開できる形（母数付き）に集計する。
    /// お客様要望: AI指数TOP5・1位指数馬の成績、TOP5の複勝率、指数帯別、中央/地方、競馬場別、
    /// 芝/ダート別、集計期間、対象レース数。モデルを変えた場合に旧モデルと新モデルを混ぜない。
    ///
    /// 数字を良く見せられない作りにしている:
    ///  - 予測は公開時点のAI指数のまま（predictionsはInsert専用）。
    ///  - 同じ予測が再生成されても、最初に公開した1件だけを数える（後から差し替えた方を選べない）。
    ///  - 的中・不的中を問わず、検証できたものを全件数える。
    /// </summary>
    public static class VerificationReportService
    {
        public static VerificationReport Build(VerificationStore store)
        {
            var picks = store.GetFirstPublishedResults();
            var report = new VerificationReport { GeneratedAtUtc = DateTime.UtcNow };

            foreach (var group in picks.GroupBy(p => p.ModelVersion ?? "(不明)").OrderBy(g => g.Key, StringComparer.Ordinal))
                report.Models.Add(BuildModel(group.Key, group.ToList()));

            return report;
        }

        private static ModelVerification BuildModel(string modelVersion, List<VerifiedPick> picks)
        {
            var model = new ModelVerification
            {
                ModelVersion = modelVersion,
                Predictions = picks.Count,
                Races = picks.Select(p => (p.RaceDate, p.TrackCode, p.RaceNumber)).Distinct().Count(),
                PeriodFrom = picks.Min(p => p.RaceDate),
                PeriodTo = picks.Max(p => p.RaceDate),
            };

            foreach (var g in picks.GroupBy(p => p.Category).OrderBy(g => CategoryOrder(g.Key)))
                model.Categories.Add(Group(g.Key, CategoryLabel(g.Key), g));

            var top5 = picks.Where(p => p.Category == "AiIndexTop5").ToList();
            foreach (var p in top5) model.Top5.Add(p);

            // 1位指数馬: 開催場・日ごとのTOP5のうち、AI指数が最も高かった馬。
            foreach (var day in top5.Where(p => p.AiIndex.HasValue).GroupBy(p => (p.RaceDate, p.TrackCode)))
                model.Top1.Add(day.OrderByDescending(p => p.AiIndex.Value).First());

            foreach (var g in top5.Where(p => p.AiIndex.HasValue)
                .GroupBy(p => Math.Max(0, Math.Min(90, (int)Math.Floor(p.AiIndex.Value / 10.0) * 10))).OrderBy(g => g.Key))
            {
                var band = new BandStat { Low = g.Key, High = g.Key + 10 };
                foreach (var p in g) band.Add(p);
                model.Bands.Add(band);
            }

            foreach (var g in top5.GroupBy(p => IsCentral(p.TrackCode) ? "central" : "local").OrderBy(g => g.Key == "central" ? 0 : 1))
                model.ByLeague.Add(Group(g.Key, g.Key == "central" ? "中央競馬" : "地方競馬", g));

            foreach (var g in top5.GroupBy(p => p.TrackCode).OrderBy(g => g.Key, StringComparer.Ordinal))
                model.ByVenue.Add(Group(g.Key, VenueNames.Get(g.Key), g));

            foreach (var g in top5.GroupBy(p => SurfaceKey(p.TrackSurfaceCode)).OrderBy(g => SurfaceOrder(g.Key)))
                model.BySurface.Add(Group(g.Key, SurfaceLabel(g.Key), g));

            return model;
        }

        private static GroupStat Group(string key, string label, IEnumerable<VerifiedPick> picks)
        {
            var stat = new GroupStat { Key = key, Label = label };
            foreach (var p in picks) stat.Add(p);
            return stat;
        }

        private static bool IsCentral(string trackCode) =>
            int.TryParse(trackCode, out var n) && n >= 1 && n <= 10;

        private static string SurfaceKey(string code)
        {
            if (string.IsNullOrEmpty(code)) return "unknown";
            switch (code.Trim()[0])
            {
                case '1': return "turf";
                case '2': return "dirt";
                default: return "other";
            }
        }

        private static int SurfaceOrder(string key) => key == "turf" ? 0 : key == "dirt" ? 1 : key == "other" ? 2 : 3;

        private static string SurfaceLabel(string key) =>
            key == "turf" ? "芝" : key == "dirt" ? "ダート" : key == "other" ? "その他（障害・ばんえい等）" : "不明（記録開始前の予測）";

        private static int CategoryOrder(string c) =>
            c == "AiIndexTop5" ? 0 : c == "Nerai" ? 1 : c == "Ana" ? 2 : c == "Kiken" ? 3 : 4;

        private static string CategoryLabel(string c) =>
            c == "AiIndexTop5" ? "AI指数TOP5" : c == "Nerai" ? "今日の狙い馬" : c == "Ana" ? "今日の穴馬" :
            c == "Kiken" ? "危険な人気馬" : c;
    }
}
