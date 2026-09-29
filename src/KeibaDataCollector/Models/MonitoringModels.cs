using System;
using System.Collections.Generic;

namespace KeibaDataCollector.Models
{
    /// <summary>仕様書§17監視ダッシュボードの表示項目一式。</summary>
    public class MonitoringSnapshot
    {
        public DateTime RaceDate { get; set; }
        public DateTime GeneratedAtUtc { get; set; }

        /// <summary>当日、scoresテーブルに算出結果がある開催場（＝当日データを処理した開催場）。
        /// JV-Link/UmaConnへ都度問い合わせるとダッシュボード表示のたびにCOM接続が発生するため、
        /// 既に蓄積済みのDBから導出する（開催があるのにまだ一度もscoreが走っていない開催場は
        /// ここには出ない点に注意。それ自体も「未処理」の一種として別途扱う）。</summary>
        public List<string> VenuesWithData { get; set; } = new List<string>();

        public DateTime? LastDataSyncUtc { get; set; }      // MAX(scores.data_cutoff_utc)
        public DateTime? LastAiComputeUtc { get; set; }      // MAX(scores.computed_at_utc)
        public DateTime? LastWordPressPublishUtc { get; set; } // MAX(predictions.created_at_utc WHERE validator_passed)

        /// <summary>ソース名（"JV-Link(中央競馬)"/"UmaConn(地方競馬)"）ごとの、直近の監査ログ上の
        /// 状態。実機へのライブpingではなく、audit_logsに記録された直近イベントからの推定。</summary>
        public Dictionary<string, string> DataSourceStatus { get; set; } = new Dictionary<string, string>();

        public bool JraLicenseVisible { get; set; }
        public List<(string VenueId, bool Visible)> LocalVenueLicenseVisible { get; set; } = new List<(string, bool)>();

        /// <summary>scoresにはあるが、predictions（狙い馬/穴馬/危険な人気馬/AI指数TOP5いずれも）が
        /// 1件も無いレース数。「算出はしたがコンテンツ化・検証がまだ」の目安。</summary>
        public int UnprocessedRaceCount { get; set; }

        public Dictionary<string, int> ErrorCountLast24h { get; set; } = new Dictionary<string, int>();

        /// <summary>当日、Validator不合格になった理由の一覧（重複除去）。</summary>
        public List<string> PublishBlockedReasons { get; set; } = new List<string>();

        public bool AutoPublishEnabled { get; set; }
    }
}
