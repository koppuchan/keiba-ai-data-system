using System;
using System.Collections.Generic;
using System.Data.SQLite;

namespace KeibaDataCollector.Data
{
    /// <summary>
    /// 仕様書§7 audit_logs（取得・計算・公開・エラー履歴）、および§16セキュリティ・§17監視
    /// ダッシュボードの情報源。Program.cs の LogFailure（全コマンドの例外処理が最終的に通る
    /// 唯一の場所）から書き込むことで、既存の各コマンド（setup/morning/predict/score/watch/
    /// backfill/trend/content/verify）すべてのエラーを個別に手直しすることなく横断的に記録する。
    /// </summary>
    public class AuditLogStore : IDisposable
    {
        private readonly SQLiteConnection _conn;
        private readonly bool _ownsConnection;

        public AuditLogStore(string dbPath)
        {
            _conn = SqliteConnections.Open(dbPath);
            _ownsConnection = true;
            EnsureSchema();
        }

        public AuditLogStore(SQLiteConnection existingConnection)
        {
            _conn = existingConnection;
            _ownsConnection = false;
            EnsureSchema();
        }

        private void EnsureSchema()
        {
            Exec(@"
                CREATE TABLE IF NOT EXISTS audit_logs (
                    event_id TEXT PRIMARY KEY,
                    occurred_at_utc TEXT NOT NULL,
                    severity TEXT NOT NULL,
                    source TEXT NOT NULL,
                    category TEXT NOT NULL,
                    message TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_audit_logs_time ON audit_logs(occurred_at_utc);
                CREATE INDEX IF NOT EXISTS idx_audit_logs_severity ON audit_logs(severity, occurred_at_utc);
            ");
        }

        public void Log(string severity, string source, string category, string message)
        {
            Exec(
                "INSERT INTO audit_logs (event_id, occurred_at_utc, severity, source, category, message) " +
                "VALUES (@id, @time, @severity, @source, @category, @message);",
                p =>
                {
                    p.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    p.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
                    p.AddWithValue("@severity", severity);
                    p.AddWithValue("@source", source);
                    p.AddWithValue("@category", category);
                    p.AddWithValue("@message", message);
                });
        }

        /// <summary>直近24時間分のseverity別件数。監視ダッシュボードの「エラー件数」に使う。</summary>
        public Dictionary<string, int> CountBySeverityLast24h()
        {
            var result = new Dictionary<string, int>();
            using (var cmd = new SQLiteCommand(
                "SELECT severity, COUNT(*) FROM audit_logs " +
                "WHERE occurred_at_utc >= @since GROUP BY severity;", _conn))
            {
                cmd.Parameters.AddWithValue("@since", DateTime.UtcNow.AddHours(-24).ToString("o"));
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        result[r.GetString(0)] = (int)r.GetInt64(1);
                }
            }
            return result;
        }

        /// <summary>指定カテゴリで最後に「成功」（severity=Info）を記録した時刻（UTC）。無ければnull。
        /// 更新停止の検知（healthcheck）と、通知メールの送り過ぎ防止に使う。</summary>
        public DateTime? LastInfoUtc(string category, string messageEquals = null)
        {
            var sql = "SELECT MAX(occurred_at_utc) FROM audit_logs WHERE severity='Info' AND category=@category";
            if (messageEquals != null) sql += " AND message=@message";
            using (var cmd = new SQLiteCommand(sql + ";", _conn))
            {
                cmd.Parameters.AddWithValue("@category", category);
                if (messageEquals != null) cmd.Parameters.AddWithValue("@message", messageEquals);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value
                    ? DateTime.Parse((string)result, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()
                    : (DateTime?)null;
            }
        }

        /// <summary>直近N件（新しい順）。監視ダッシュボードでの最終確認・トラブルシュート用。</summary>
        public List<(DateTime OccurredAtUtc, string Severity, string Source, string Category, string Message)> GetRecent(int limit)
        {
            var result = new List<(DateTime, string, string, string, string)>();
            using (var cmd = new SQLiteCommand(
                "SELECT occurred_at_utc, severity, source, category, message FROM audit_logs " +
                "ORDER BY occurred_at_utc DESC LIMIT @limit;", _conn))
            {
                cmd.Parameters.AddWithValue("@limit", limit);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        result.Add((DateTime.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
                    }
                }
            }
            return result;
        }

        private void Exec(string sql, Action<SQLiteParameterCollection> bind = null)
        {
            using (var cmd = new SQLiteCommand(sql, _conn))
            {
                bind?.Invoke(cmd.Parameters);
                cmd.ExecuteNonQuery();
            }
        }

        public void Dispose()
        {
            if (_ownsConnection)
            {
                _conn?.Close();
                _conn?.Dispose();
            }
        }
    }
}
