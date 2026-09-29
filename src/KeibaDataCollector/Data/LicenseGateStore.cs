using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using KeibaDataCollector.Models;

namespace KeibaDataCollector.Data
{
    /// <summary>
    /// 仕様書§5 LicenseGate の永続化層。
    ///
    /// 大前提: 「データを取得できる」と「そのデータをWebで公開できる」は別問題として扱う
    /// （仕様書末尾「開発者への最終指示」）。このクラスはその判定だけを担い、実際の取得処理・
    /// 公開処理には一切関与しない（Source AdapterがJV-Link/UmaConnから取得し、Publisherが
    /// このクラスの IsWebPublishAllowed() を呼んで判定を仰ぐ）。
    ///
    /// フェイルクローズが必須方針: 設定行が無い・承認状態が不明な場合は「非公開」を返す。
    /// 「取得さえできれば公開してよい」という設計には絶対にしない。
    ///
    /// DBファイルはHistoricalDataStoreと同じ historical.sqlite3 を共有する。このクラス単体でも
    /// 新規DBに対してテーブルを作成できるようEnsureSchemaを独立して持たせている。
    /// </summary>
    public class LicenseGateStore : IDisposable
    {
        private readonly SQLiteConnection _conn;
        private readonly bool _ownsConnection;

        public LicenseGateStore(string dbPath)
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _conn = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            _conn.Open();
            _ownsConnection = true;

            EnsureSchema();
        }

        /// <summary>HistoricalDataStore等、既に開いているコネクションに相乗りする場合用。</summary>
        public LicenseGateStore(SQLiteConnection existingConnection)
        {
            _conn = existingConnection;
            _ownsConnection = false;
            EnsureSchema();
        }

        private void EnsureSchema()
        {
            Exec(@"
                CREATE TABLE IF NOT EXISTS jra_license (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    acquisition_state TEXT NOT NULL DEFAULT 'inactive',
                    web_publish_approval TEXT NOT NULL DEFAULT 'pending',
                    commercial_use_approval TEXT NOT NULL DEFAULT 'pending',
                    note TEXT,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS local_venue_license (
                    venue_id TEXT PRIMARY KEY,
                    venue_name TEXT,
                    contract_state TEXT NOT NULL DEFAULT 'inactive',
                    web_publish_approval TEXT NOT NULL DEFAULT 'pending',
                    note TEXT,
                    updated_at_utc TEXT NOT NULL
                );
            ");
        }

        // ---- JRA（中央競馬） ----

        public JraLicenseState GetJraLicense()
        {
            using (var cmd = new SQLiteCommand(
                "SELECT acquisition_state, web_publish_approval, commercial_use_approval, note, updated_at_utc " +
                "FROM jra_license WHERE id = 1;", _conn))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read())
                    return null; // 行が無い = 未設定。呼び出し側（IsWebPublishAllowed）はこれを非公開として扱う。

                return new JraLicenseState
                {
                    AcquisitionState = ParseAcquisition(r.GetString(0)),
                    WebPublishApproval = ParseApproval(r.GetString(1)),
                    CommercialUseApproval = ParseApproval(r.GetString(2)),
                    Note = r.IsDBNull(3) ? null : r.GetString(3),
                    UpdatedAtUtc = DateTime.Parse(r.GetString(4)),
                };
            }
        }

        /// <summary>状態を丸ごと置き換える。管理者が明示的に操作したときのみ呼ぶこと
        /// （Program.cs の licensegate コマンド、または将来の管理画面から）。</summary>
        public void SetJraLicense(JraLicenseState state, string note = null)
        {
            Exec(@"
                INSERT INTO jra_license (id, acquisition_state, web_publish_approval, commercial_use_approval, note, updated_at_utc)
                VALUES (1, @acq, @web, @commercial, @note, @updated)
                ON CONFLICT(id) DO UPDATE SET
                    acquisition_state=excluded.acquisition_state,
                    web_publish_approval=excluded.web_publish_approval,
                    commercial_use_approval=excluded.commercial_use_approval,
                    note=excluded.note,
                    updated_at_utc=excluded.updated_at_utc;
            ",
                p =>
                {
                    p.AddWithValue("@acq", state.AcquisitionState.ToString().ToLowerInvariant());
                    p.AddWithValue("@web", state.WebPublishApproval.ToString().ToLowerInvariant());
                    p.AddWithValue("@commercial", state.CommercialUseApproval.ToString().ToLowerInvariant());
                    p.AddWithValue("@note", (object)(note ?? state.Note) ?? DBNull.Value);
                    p.AddWithValue("@updated", DateTime.UtcNow.ToString("o"));
                });
        }

        // ---- 地方競馬（venue単位） ----

        public LocalVenueLicenseState GetLocalVenueLicense(string venueId)
        {
            using (var cmd = new SQLiteCommand(
                "SELECT venue_id, venue_name, contract_state, web_publish_approval, note, updated_at_utc " +
                "FROM local_venue_license WHERE venue_id = @venue;", _conn))
            {
                cmd.Parameters.AddWithValue("@venue", venueId);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read())
                        return null; // 行が無い = 契約対象外。呼び出し側は非公開として扱う。

                    return new LocalVenueLicenseState
                    {
                        VenueId = r.GetString(0),
                        VenueName = r.IsDBNull(1) ? null : r.GetString(1),
                        ContractState = ParseAcquisition(r.GetString(2)),
                        WebPublishApproval = ParseApproval(r.GetString(3)),
                        Note = r.IsDBNull(4) ? null : r.GetString(4),
                        UpdatedAtUtc = DateTime.Parse(r.GetString(5)),
                    };
                }
            }
        }

        public List<LocalVenueLicenseState> ListLocalVenueLicenses()
        {
            var result = new List<LocalVenueLicenseState>();
            using (var cmd = new SQLiteCommand(
                "SELECT venue_id, venue_name, contract_state, web_publish_approval, note, updated_at_utc " +
                "FROM local_venue_license ORDER BY venue_id;", _conn))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    result.Add(new LocalVenueLicenseState
                    {
                        VenueId = r.GetString(0),
                        VenueName = r.IsDBNull(1) ? null : r.GetString(1),
                        ContractState = ParseAcquisition(r.GetString(2)),
                        WebPublishApproval = ParseApproval(r.GetString(3)),
                        Note = r.IsDBNull(4) ? null : r.GetString(4),
                        UpdatedAtUtc = DateTime.Parse(r.GetString(5)),
                    });
                }
            }
            return result;
        }

        public void SetLocalVenueLicense(LocalVenueLicenseState state, string note = null)
        {
            if (string.IsNullOrEmpty(state.VenueId))
                throw new ArgumentException("VenueId は必須です。", nameof(state));

            Exec(@"
                INSERT INTO local_venue_license (venue_id, venue_name, contract_state, web_publish_approval, note, updated_at_utc)
                VALUES (@venue, @name, @contract, @web, @note, @updated)
                ON CONFLICT(venue_id) DO UPDATE SET
                    venue_name=excluded.venue_name,
                    contract_state=excluded.contract_state,
                    web_publish_approval=excluded.web_publish_approval,
                    note=excluded.note,
                    updated_at_utc=excluded.updated_at_utc;
            ",
                p =>
                {
                    p.AddWithValue("@venue", state.VenueId);
                    p.AddWithValue("@name", (object)state.VenueName ?? DBNull.Value);
                    p.AddWithValue("@contract", state.ContractState.ToString().ToLowerInvariant());
                    p.AddWithValue("@web", state.WebPublishApproval.ToString().ToLowerInvariant());
                    p.AddWithValue("@note", (object)(note ?? state.Note) ?? DBNull.Value);
                    p.AddWithValue("@updated", DateTime.UtcNow.ToString("o"));
                });
        }

        // ---- ゲート判定（Publisherから呼ばれる本体） ----

        /// <summary>この開催場のレースをWordPressへ公開してよいかを判定する。
        /// 中央競馬か地方競馬かで参照するテーブルが変わる。
        /// 判定不能・未設定・却下のいずれも false（非公開）に倒す。「取得できる」ことは
        /// この判定には一切影響しない（取得可否はSource Adapter側の別問題）。</summary>
        public bool IsWebPublishAllowed(string venueId, bool isCentral)
        {
            if (isCentral)
            {
                var jra = GetJraLicense();
                if (jra == null) return false;
                return jra.AcquisitionState == AcquisitionState.Active
                    && jra.WebPublishApproval == ApprovalState.Approved
                    && jra.CommercialUseApproval == ApprovalState.Approved;
            }

            var local = GetLocalVenueLicense(venueId);
            if (local == null) return false; // 契約対象外の場を誤って公開しない（仕様書§21受け入れ基準）
            return local.ContractState == AcquisitionState.Active
                && local.WebPublishApproval == ApprovalState.Approved;
        }

        private static AcquisitionState ParseAcquisition(string s) =>
            (AcquisitionState)Enum.Parse(typeof(AcquisitionState), s, ignoreCase: true);

        private static ApprovalState ParseApproval(string s) =>
            (ApprovalState)Enum.Parse(typeof(ApprovalState), s, ignoreCase: true);

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
