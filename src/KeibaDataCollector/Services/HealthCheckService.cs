using System;
using System.Data.SQLite;
using KeibaDataCollector.Data;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 「一定時間以上、更新が止まっていないか」を見る監視（お客様要望: 一時的なエラーではなく、
    /// 止まった状態が続く場合に通知する）。個々の実行の失敗はダッシュボード・ログへの記録に留め、
    /// 開催日の日中にAI指数の算出またはWordPress公開が一定時間成功していないときだけ
    /// Critical（メール通知）にする。
    ///
    /// COM（JV-Link/UmaConn）には接続せず、すでにある記録（audit_logs・scores・race_entries）だけを読む。
    /// </summary>
    public class HealthCheckService
    {
        // 監視する時間帯。朝のタスク開始直後（まだ1回も成功していない時間）の誤報を避ける。
        private static readonly TimeSpan WindowStart = new TimeSpan(8, 30, 0);
        private static readonly TimeSpan WindowEnd = new TimeSpan(23, 0, 0);

        // Score/Contentは20分おきに動く。この時間成功が無ければ「止まっている」とみなす。
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(60);

        // 止まったままの間は、この間隔でだけ再通知する。
        private static readonly TimeSpan RenotifyAfter = TimeSpan.FromHours(2);

        private readonly SQLiteConnection _conn;
        private readonly AuditLogStore _auditLog;
        private readonly NotifierService _notifier;

        public HealthCheckService(SQLiteConnection conn, AuditLogStore auditLog, NotifierService notifier)
        {
            _conn = conn;
            _auditLog = auditLog;
            _notifier = notifier;
        }

        public void Run(DateTime now)
        {
            if (now.TimeOfDay < WindowStart || now.TimeOfDay > WindowEnd)
            {
                Console.WriteLine($"[healthcheck] 監視時間外（{WindowStart:hh\\:mm}〜{WindowEnd:hh\\:mm}）のため確認しません。");
                return;
            }

            if (!HasRacesToday(now))
            {
                Console.WriteLine("[healthcheck] 本日は開催が確認できないため、更新停止の確認は行いません。");
                return;
            }

            var stalled = false;
            stalled |= CheckStage(now, "AI指数算出", "AI指数の算出");
            stalled |= CheckStage(now, "コンテンツ生成・公開", "WordPressへの公開");

            if (!stalled)
                Console.WriteLine("[healthcheck] 異常なし（AI指数の算出・WordPressへの公開とも直近で成功しています）。");
        }

        private bool CheckStage(DateTime now, string category, string label)
        {
            var last = _auditLog.LastInfoUtc(category);
            var lastLocal = last?.ToLocalTime();

            if (lastLocal.HasValue && now - lastLocal.Value < StaleAfter)
                return false;

            var since = lastLocal.HasValue ? $"最後の成功: {lastLocal:yyyy/MM/dd HH:mm}" : "成功の記録がありません";
            _notifier.Notify(
                NotifierService.SeverityCritical,
                "healthcheck",
                $"自動更新停止（{label}）",
                $"{label}が{(int)StaleAfter.TotalMinutes}分以上成功していません（{since}）。" +
                "開催日の日中に自動更新が止まっている可能性があります。VPSのタスクスケジューラとログ（logsフォルダ）を確認してください。",
                RenotifyAfter);
            return true;
        }

        /// <summary>今日の出走表（race_entries）か算出結果（scores）が、DBに入っているか。</summary>
        private bool HasRacesToday(DateTime now)
        {
            var today = now.ToString("yyyy-MM-dd");
            foreach (var sql in new[]
            {
                "SELECT 1 FROM race_entries WHERE race_date=@date LIMIT 1;",
                "SELECT 1 FROM scores WHERE race_date=@date LIMIT 1;",
            })
            {
                using (var cmd = new SQLiteCommand(sql, _conn))
                {
                    cmd.Parameters.AddWithValue("@date", today);
                    if (cmd.ExecuteScalar() != null) return true;
                }
            }
            return false;
        }
    }
}
