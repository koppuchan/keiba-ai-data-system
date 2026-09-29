using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KeibaDataCollector.Data;
using KeibaDataCollector.Models;
using KeibaDataCollector.WordPress;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 仕様書§13 WordPress自動公開。AI指数TOP5・本日の傾向・Validator通過済みの狙い馬/穴馬/危険な
    /// 人気馬を1開催場・1日分のダイジェストにまとめ、WordPressへidempotentに反映する。
    ///
    /// 公開直前のゲートをここに集約する:
    ///   1. LicenseGate（仕様書§5・§21「契約範囲外の競馬場を誤って公開しない」）
    ///   2. 自動公開ON/OFF（仕様書§13。WordPress管理画面のチェックボックスと連動）
    ///   3. Validator未通過のピックを含めない（Validator自体はIssue #6で実装済み。
    ///      ここでは「合格したものだけを渡す」という契約を呼び出し側に課す）
    ///   4. 空コンテンツなら送信しない（DigestPayload.HasAnyContent）
    /// </summary>
    public class DigestPublisherService
    {
        private readonly WordPressClient _wp;
        private readonly LicenseGateStore _licenseGate;
        private readonly PredictionStore _predictions;

        public DigestPublisherService(WordPressClient wp, LicenseGateStore licenseGate, PredictionStore predictions)
        {
            _wp = wp;
            _licenseGate = licenseGate;
            _predictions = predictions;
        }

        public async Task<PublishOutcome> PublishAsync(
            DateTime raceDate,
            string trackCode,
            bool isCentral,
            List<AiIndexResult> top5,
            VenueTrendSnapshot trendMorning,
            VenueTrendSnapshot trendLive,
            VenueTrendSnapshot trendFinal,
            List<(GeneratedPick Pick, ValidationOutcome Outcome)> validatedPicks)
        {
            var licenseVisible = _licenseGate.IsWebPublishAllowed(trackCode, isCentral);
            if (!licenseVisible)
                return PublishOutcome.Skipped("LicenseGate: この開催場は現在Web公開が許可されていません。");

            if (!await _wp.IsAutoPublishEnabledAsync())
                return PublishOutcome.Skipped("自動公開がOFFになっています（WordPress管理画面で確認してください）。");

            var passedPicks = validatedPicks.Where(x => x.Outcome.Passed).Select(x => x.Pick).ToList();
            var blockedCount = validatedPicks.Count - passedPicks.Count;

            var payload = new DigestPayload
            {
                RaceDate = raceDate,
                TrackCode = trackCode,
                AiIndexTop5 = top5 ?? new List<AiIndexResult>(),
                TrendMorning = trendMorning,
                TrendLive = trendLive,
                TrendFinal = trendFinal,
                Picks = passedPicks,
                LicenseVisible = true,
            };

            if (!payload.HasAnyContent)
                return PublishOutcome.Skipped("公開対象のコンテンツが1件もありません（AI指数TOP5・傾向・ピックすべて空）。");

            // AI指数TOP5も仕様書§18のレース後自動検証（Issue #8）の対象にするため、公開するものは
            // predictionsへimmutableにsnapshotしておく。狙い馬/穴馬/危険な人気馬はIssue #6の
            // ValidatorService.ValidateAndSnapshotで既にsnapshot済みだが、AI指数TOP5にはこれまで
            // snapshotの機会が無かった（Validatorを経由しない。TOP5は「生成→後で再照合」ではなく
            // 「今のDBの値をそのまま出す」ものなので、Validator相当のDB再照合は不要と判断し、
            // ここで直接snapshotする）。
            if (top5 != null)
            {
                foreach (var t in top5)
                    SnapshotAiIndexTop5(t, licenseVisible);
            }

            var published = await _wp.UpsertDigestAsync(payload);
            return published
                ? PublishOutcome.Published(passedPicks.Count, blockedCount)
                : PublishOutcome.Skipped("送信条件を満たさず見送りました。");
        }

        private void SnapshotAiIndexTop5(AiIndexResult t, bool licenseVisible)
        {
            _predictions.Insert(new PredictionRecord
            {
                PredictionId = Guid.NewGuid().ToString("N"),
                RaceDate = t.RaceDate,
                TrackCode = t.TrackCode,
                RaceNumber = t.RaceNumber,
                Umaban = t.Umaban,
                KettoNum = t.KettoNum,
                Category = "AiIndexTop5",
                ContentText = $"AI指数TOP5: {t.RaceNumber}R {t.Umaban}番 指数{t.AiIndex:0.1}",
                Reasons = new List<string> { $"ai_index={t.AiIndex:0.00}", $"data_completeness={t.DataCompleteness:0.00}" },
                AiIndexSnapshot = t.AiIndex,
                ModelVersion = t.ModelVersion,
                LicenseCheckPassed = licenseVisible,
                ValidatorPassed = true, // TOP5は現在のDB値そのものを公開するため、再照合の概念が無い。
                ValidatorNotes = null,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }
    }

    public class PublishOutcome
    {
        public bool WasPublished { get; private set; }
        public string SkipReason { get; private set; }
        public int PublishedPickCount { get; private set; }
        public int BlockedPickCount { get; private set; }

        public static PublishOutcome Published(int publishedPickCount, int blockedPickCount) =>
            new PublishOutcome { WasPublished = true, PublishedPickCount = publishedPickCount, BlockedPickCount = blockedPickCount };

        public static PublishOutcome Skipped(string reason) =>
            new PublishOutcome { WasPublished = false, SkipReason = reason };
    }
}
