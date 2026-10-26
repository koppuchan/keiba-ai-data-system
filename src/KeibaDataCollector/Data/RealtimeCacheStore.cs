using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using KeibaDataCollector.Interop;

namespace KeibaDataCollector.Data
{
    /// <summary>
    /// 速報データ（0B31の単勝オッズ、0B12の確定成績）の取得済みの分を保存して、次の周期で再利用する。
    ///
    /// 周期（cycle）は20分ごとに動くが、1回の速報の問い合わせ（JVRTOpen）に数秒〜十数秒かかり、
    /// 開催場×全レースを毎回取り直すと、それだけで周期が20分を超えていた（実測 2026-10-26）。
    /// 発走後のレースのオッズ、確定済みレースの成績は、もう変わらないので取り直さない。
    /// 提供元（UmaConn/JV-Link）へのアクセスも減る。
    /// </summary>
    public class RealtimeCacheStore : IDisposable
    {
        private readonly SQLiteConnection _conn;

        public RealtimeCacheStore(string dbPath)
        {
            _conn = SqliteConnections.Open(dbPath);
            EnsureSchema();
        }

        private void EnsureSchema()
        {
            Exec(@"
                CREATE TABLE IF NOT EXISTS race_odds_cache (
                    race_date TEXT NOT NULL,
                    track_code TEXT NOT NULL,
                    race_number INTEGER NOT NULL,
                    umaban INTEGER NOT NULL,
                    ninki INTEGER NOT NULL,
                    odds REAL NOT NULL,
                    fetched_at_utc TEXT NOT NULL,
                    PRIMARY KEY (race_date, track_code, race_number, umaban)
                );
                CREATE TABLE IF NOT EXISTS race_result_cache (
                    race_date TEXT NOT NULL,
                    track_code TEXT NOT NULL,
                    race_number INTEGER NOT NULL,
                    payload TEXT NOT NULL,
                    fetched_at_utc TEXT NOT NULL,
                    PRIMARY KEY (race_date, track_code, race_number)
                );
                DELETE FROM race_odds_cache WHERE race_date < date('now', '-7 day');
                DELETE FROM race_result_cache WHERE race_date < date('now', '-7 day');
            ");
        }

        private static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // ---- オッズ ----

        public void SaveOdds(DateTime raceDate, string trackCode, int raceNumber, Dictionary<int, JvRecordParser.TanshoOdds> odds)
        {
            if (odds == null || odds.Count == 0) return;
            using (var tx = _conn.BeginTransaction())
            {
                foreach (var kv in odds)
                {
                    using (var cmd = new SQLiteCommand(@"
                        INSERT INTO race_odds_cache (race_date, track_code, race_number, umaban, ninki, odds, fetched_at_utc)
                        VALUES (@d, @t, @r, @u, @n, @o, @f)
                        ON CONFLICT(race_date, track_code, race_number, umaban) DO UPDATE SET
                            ninki=excluded.ninki, odds=excluded.odds, fetched_at_utc=excluded.fetched_at_utc;", _conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@d", Day(raceDate));
                        cmd.Parameters.AddWithValue("@t", trackCode);
                        cmd.Parameters.AddWithValue("@r", raceNumber);
                        cmd.Parameters.AddWithValue("@u", kv.Key);
                        cmd.Parameters.AddWithValue("@n", kv.Value.Ninki);
                        cmd.Parameters.AddWithValue("@o", kv.Value.Odds);
                        cmd.Parameters.AddWithValue("@f", DateTime.UtcNow.ToString("o"));
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
        }

        /// <summary>保存済みのオッズ。無ければnull。</summary>
        public Dictionary<int, JvRecordParser.TanshoOdds> GetOdds(DateTime raceDate, string trackCode, int raceNumber)
        {
            Dictionary<int, JvRecordParser.TanshoOdds> result = null;
            using (var cmd = new SQLiteCommand(
                "SELECT umaban, ninki, odds FROM race_odds_cache WHERE race_date=@d AND track_code=@t AND race_number=@r;", _conn))
            {
                cmd.Parameters.AddWithValue("@d", Day(raceDate));
                cmd.Parameters.AddWithValue("@t", trackCode);
                cmd.Parameters.AddWithValue("@r", raceNumber);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if (result == null) result = new Dictionary<int, JvRecordParser.TanshoOdds>();
                        result[r.GetInt32(0)] = new JvRecordParser.TanshoOdds { Ninki = r.GetInt32(1), Odds = r.GetDouble(2) };
                    }
                }
            }
            return result;
        }

        // ---- 確定成績（JSON文字列で保存。中身は呼び出し側が決める） ----

        public void SaveResult(DateTime raceDate, string trackCode, int raceNumber, string payload)
        {
            Exec(@"
                INSERT INTO race_result_cache (race_date, track_code, race_number, payload, fetched_at_utc)
                VALUES (@d, @t, @r, @p, @f)
                ON CONFLICT(race_date, track_code, race_number) DO UPDATE SET
                    payload=excluded.payload, fetched_at_utc=excluded.fetched_at_utc;",
                c =>
                {
                    c.AddWithValue("@d", Day(raceDate));
                    c.AddWithValue("@t", trackCode);
                    c.AddWithValue("@r", raceNumber);
                    c.AddWithValue("@p", payload);
                    c.AddWithValue("@f", DateTime.UtcNow.ToString("o"));
                });
        }

        public string GetResult(DateTime raceDate, string trackCode, int raceNumber)
        {
            using (var cmd = new SQLiteCommand(
                "SELECT payload FROM race_result_cache WHERE race_date=@d AND track_code=@t AND race_number=@r;", _conn))
            {
                cmd.Parameters.AddWithValue("@d", Day(raceDate));
                cmd.Parameters.AddWithValue("@t", trackCode);
                cmd.Parameters.AddWithValue("@r", raceNumber);
                return cmd.ExecuteScalar() as string;
            }
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
            _conn?.Close();
            _conn?.Dispose();
        }
    }
}
