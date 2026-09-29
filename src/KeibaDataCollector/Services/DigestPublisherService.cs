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

        public DigestPublisherService(WordPressClient wp, LicenseGateStore licenseGate)
        {
            _wp = wp;
            _licenseGate = licenseGate;
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

            var published = await _wp.UpsertDigestAsync(payload);
            return published
                ? PublishOutcome.Published(passedPicks.Count, blockedCount)
                : PublishOutcome.Skipped("送信条件を満たさず見送りました。");
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
