using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;
using KeibaDataCollector.Data;
using KeibaDataCollector.Models;
using KeibaDataCollector.WordPress;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 仕様書§17監視ダッシュボードのスナップショットを組み立てる。すでにある各テーブル
    /// （scores/predictions/audit_logs/license_gate）を横断的に読むだけの読み取り専用サービスで、
    /// 新しい状態は持たない。
    ///
    /// 「当日開催場」等の判定はJV-Link/UmaConnへ都度ライブ問い合わせせず、既に蓄積済みのDBから
    /// 導出する。ダッシュボード確認のたびにCOM接続を発生させたくないため（実機のJV-Link/UmaConnは
    /// 1台のPCに紐付く排他的なリソースで、watch等の本処理と競合させたくない）。
    /// </summary>
    public class MonitoringService
    {
        private readonly SQLiteConnection _conn;
        private readonly LicenseGateStore _licenseGate;
        private readonly AuditLogStore _auditLog;
        private readonly WordPressClient _wp;

        public MonitoringService(SQLiteConnection conn, LicenseGateStore licenseGate, AuditLogStore auditLog, WordPressClient wp)
        {
            _conn = conn;
            _licenseGate = licenseGate;
            _auditLog = auditLog;
            _wp = wp;
        }

        public async Task<MonitoringSnapshot> BuildSnapshotAsync(DateTime raceDate)
        {
            var snapshot = new MonitoringSnapshot
            {
                RaceDate = raceDate,
                GeneratedAtUtc = DateTime.UtcNow,
            };

            var dateStr = raceDate.ToString("yyyy-MM-dd");

            snapshot.VenuesWithData = QueryStrings(
                "SELECT DISTINCT track_code FROM scores WHERE race_date=@date ORDER BY track_code;", dateStr);

            snapshot.LastDataSyncUtc = QueryMaxDate(
                "SELECT MAX(data_cutoff_utc) FROM scores WHERE race_date=@date;", dateStr);
            snapshot.LastAiComputeUtc = QueryMaxDate(
                "SELECT MAX(computed_at_utc) FROM scores WHERE race_date=@date;", dateStr);
            snapshot.LastWordPressPublishUtc = QueryMaxDate(
                "SELECT MAX(created_at_utc) FROM predictions WHERE race_date=@date AND validator_passed=1;", dateStr);

            foreach (var source in new[] { "JV-Link(中央競馬)", "UmaConn(地方競馬)" })
                snapshot.DataSourceStatus[source] = QueryLatestSourceStatus(source);

            var jra = _licenseGate.GetJraLicense();
            snapshot.JraLicenseVisible = jra != null
                && jra.AcquisitionState == Models.AcquisitionState.Active
                && jra.WebPublishApproval == Models.ApprovalState.Approved
                && jra.CommercialUseApproval == Models.ApprovalState.Approved;

            foreach (var v in _licenseGate.ListLocalVenueLicenses())
                snapshot.LocalVenueLicenseVisible.Add((v.VenueId, _licenseGate.IsWebPublishAllowed(v.VenueId, isCentral: false)));

            snapshot.UnprocessedRaceCount = QueryCount(@"
                SELECT COUNT(*) FROM (
                    SELECT DISTINCT race_date, track_code, race_number FROM scores WHERE race_date=@date
                    EXCEPT
                    SELECT DISTINCT race_date, track_code, race_number FROM predictions WHERE race_date=@date
                );", dateStr);

            snapshot.ErrorCountLast24h = _auditLog.CountBySeverityLast24h();

            snapshot.PublishBlockedReasons = QueryStrings(
                "SELECT DISTINCT validator_notes FROM predictions WHERE race_date=@date AND validator_passed=0 AND validator_notes IS NOT NULL;",
                dateStr);

            snapshot.AutoPublishEnabled = await _wp.IsAutoPublishEnabledAsync();

            return snapshot;
        }

        private string QueryLatestSourceStatus(string source)
        {
            using (var cmd = new SQLiteCommand(
                "SELECT severity FROM audit_logs WHERE source=@source ORDER BY occurred_at_utc DESC LIMIT 1;", _conn))
            {
                cmd.Parameters.AddWithValue("@source", source);
                var result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value) return "記録なし";
                var severity = (string)result;
                return severity == NotifierService.SeverityInfo ? "正常" : $"直近のイベントは{severity}";
            }
        }

        private List<string> QueryStrings(string sql, string dateParam)
        {
            var result = new List<string>();
            using (var cmd = new SQLiteCommand(sql, _conn))
            {
                cmd.Parameters.AddWithValue("@date", dateParam);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        if (!r.IsDBNull(0)) result.Add(r.GetString(0));
                }
            }
            return result;
        }

        private DateTime? QueryMaxDate(string sql, string dateParam)
        {
            using (var cmd = new SQLiteCommand(sql, _conn))
            {
                cmd.Parameters.AddWithValue("@date", dateParam);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? DateTime.Parse((string)result) : (DateTime?)null;
            }
        }

        private int QueryCount(string sql, string dateParam)
        {
            using (var cmd = new SQLiteCommand(sql, _conn))
            {
                cmd.Parameters.AddWithValue("@date", dateParam);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
            }
        }
    }
}
