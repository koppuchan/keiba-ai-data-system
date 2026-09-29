using System;
using System.Collections.Generic;
using System.Data.SQLite;
using KeibaDataCollector.Models;

namespace KeibaDataCollector.Data
{
    /// <summary>
    /// 仕様書§18 レース後の自動検証。predictions（Issue #6・#7でimmutableに保存済み）1件ごとに、
    /// 確定着順と突き合わせた結果を保持する。
    ///
    /// upsertを許しているのは「同じpredictionに対する検証を後から書き換える」ためではなく、
    /// 「VerificationServiceを複数回実行しても同じ結果に収束する（冪等）」ようにするため。
    /// 入力（predictions.*は不変、確定着順も一度確定すれば変わらない）が変わらない限り、
    /// 出力も変わらない。まだ確定していないレースの行は作らない（chakujunが取れるまで待つ）。
    /// </summary>
    public class VerificationStore : IDisposable
    {
        private readonly SQLiteConnection _conn;
        private readonly bool _ownsConnection;

        public VerificationStore(string dbPath)
        {
            var dir = System.IO.Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            _conn = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            _conn.Open();
            _ownsConnection = true;
            EnsureSchema();
        }

        public VerificationStore(SQLiteConnection existingConnection)
        {
            _conn = existingConnection;
            _ownsConnection = false;
            EnsureSchema();
        }

        private void EnsureSchema()
        {
            Exec(@"
                CREATE TABLE IF NOT EXISTS verification (
                    prediction_id TEXT PRIMARY KEY,
                    race_date TEXT NOT NULL,
                    track_code TEXT NOT NULL,
                    race_number INTEGER NOT NULL,
                    umaban INTEGER NOT NULL,
                    category TEXT NOT NULL,
                    model_version TEXT,
                    chakujun INTEGER NOT NULL,
                    hit_top3 INTEGER NOT NULL,
                    hit_win INTEGER NOT NULL,
                    verified_at_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_verification_model
                    ON verification(model_version, category);
            ");
        }

        public void Upsert(VerificationRecord record)
        {
            Exec(@"
                INSERT INTO verification
                    (prediction_id, race_date, track_code, race_number, umaban, category,
                     model_version, chakujun, hit_top3, hit_win, verified_at_utc)
                VALUES
                    (@id, @date, @track, @raceNum, @umaban, @category,
                     @modelVer, @chakujun, @top3, @win, @verified)
                ON CONFLICT(prediction_id) DO UPDATE SET
                    chakujun=excluded.chakujun,
                    hit_top3=excluded.hit_top3,
                    hit_win=excluded.hit_win,
                    verified_at_utc=excluded.verified_at_utc;
            ",
                p =>
                {
                    p.AddWithValue("@id", record.PredictionId);
                    p.AddWithValue("@date", record.RaceDate.ToString("yyyy-MM-dd"));
                    p.AddWithValue("@track", record.TrackCode);
                    p.AddWithValue("@raceNum", record.RaceNumber);
                    p.AddWithValue("@umaban", record.Umaban);
                    p.AddWithValue("@category", record.Category);
                    p.AddWithValue("@modelVer", (object)record.ModelVersion ?? DBNull.Value);
                    p.AddWithValue("@chakujun", record.Chakujun);
                    p.AddWithValue("@top3", record.HitTop3 ? 1 : 0);
                    p.AddWithValue("@win", record.HitWin ? 1 : 0);
                    p.AddWithValue("@verified", record.VerifiedAtUtc.ToString("o"));
                });
        }

        /// <summary>すでに検証済みのprediction_idの集合。VerificationServiceが同じ予測を
        /// 何度も検証しようとして無駄なクエリを重ねないようにするためのチェック用
        /// （Upsert自体は冪等だが、確定着順の再取得コストを避ける）。</summary>
        public HashSet<string> GetVerifiedPredictionIds(DateTime raceDate, string trackCode)
        {
            var set = new HashSet<string>();
            using (var cmd = new SQLiteCommand(
                "SELECT prediction_id FROM verification WHERE race_date=@date AND track_code=@track;", _conn))
            {
                cmd.Parameters.AddWithValue("@date", raceDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@track", trackCode);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) set.Add(r.GetString(0));
                }
            }
            return set;
        }

        /// <summary>仕様書§18「指数帯別成績をmodel_versionごとに分離」。predictionsとverificationを
        /// ai_index_snapshotでJOINし、10点刻みの帯ごとに母数付きで集計する。
        /// カテゴリ（Nerai/Ana/Kiken/AiIndexTop5）ごとに分けて見たいことが多いため、categoryも指定させる。</summary>
        public List<IndexBandStat> GetIndexBandStats(string modelVersion, string category)
        {
            var stats = new Dictionary<int, IndexBandStat>();
            using (var cmd = new SQLiteCommand(@"
                SELECT p.ai_index_snapshot, v.hit_top3, v.hit_win
                FROM verification v
                JOIN predictions p ON p.prediction_id = v.prediction_id
                WHERE v.model_version = @modelVer AND v.category = @category
                      AND p.ai_index_snapshot IS NOT NULL;", _conn))
            {
                cmd.Parameters.AddWithValue("@modelVer", modelVersion);
                cmd.Parameters.AddWithValue("@category", category);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var index = r.GetDouble(0);
                        var band = (int)Math.Floor(index / 10.0) * 10;
                        band = Math.Max(0, Math.Min(90, band));

                        if (!stats.TryGetValue(band, out var stat))
                        {
                            stat = new IndexBandStat { ModelVersion = modelVersion, Category = category, BandLow = band, BandHigh = band + 10 };
                            stats[band] = stat;
                        }
                        stat.SampleCount++;
                        if (r.GetInt32(1) != 0) stat.Top3Count++;
                        if (r.GetInt32(2) != 0) stat.WinCount++;
                    }
                }
            }

            var result = new List<IndexBandStat>(stats.Values);
            result.Sort((a, b) => a.BandLow.CompareTo(b.BandLow));
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
