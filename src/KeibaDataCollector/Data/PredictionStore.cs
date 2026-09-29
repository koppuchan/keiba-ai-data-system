using System;
using System.Data.SQLite;
using KeibaDataCollector.Models;
using Newtonsoft.Json;

namespace KeibaDataCollector.Data
{
    /// <summary>
    /// 仕様書§7データモデルの predictions テーブル（Verification DBの一部）。
    ///
    /// 予測生成時点のデータ（AI指数・人気・オッズ・文章・根拠）をimmutableに保存する
    /// （仕様書§14「予測生成時のデータをimmutable snapshotとして保存する。レース後に過去の
    /// 予測内容を書き換えない」、§18「予測時点のオッズ・馬場・指数・コメントを保存。後から
    /// 都合の良いデータへ書き換えない」）。
    ///
    /// そのためInsertのみを公開し、Update系のメソッドは意図的に用意していない。同じ内容を
    /// 再生成した場合は新しいprediction_idで別行として追加する（既存行は一切変更しない）。
    /// レース後の着順突き合わせ（仕様書§18のverificationテーブル）はIssue #8で別途実装する。
    /// </summary>
    public class PredictionStore : IDisposable
    {
        private readonly SQLiteConnection _conn;
        private readonly bool _ownsConnection;

        public PredictionStore(string dbPath)
        {
            var dir = System.IO.Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            _conn = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            _conn.Open();
            _ownsConnection = true;
            EnsureSchema();
        }

        public PredictionStore(SQLiteConnection existingConnection)
        {
            _conn = existingConnection;
            _ownsConnection = false;
            EnsureSchema();
        }

        private void EnsureSchema()
        {
            Exec(@"
                CREATE TABLE IF NOT EXISTS predictions (
                    prediction_id TEXT PRIMARY KEY,
                    race_date TEXT NOT NULL,
                    track_code TEXT NOT NULL,
                    race_number INTEGER NOT NULL,
                    umaban INTEGER NOT NULL,
                    ketto_num TEXT,
                    horse_name TEXT,
                    jockey_code TEXT,
                    category TEXT NOT NULL,
                    content_text TEXT NOT NULL,
                    reasons_json TEXT NOT NULL,
                    ai_index_snapshot REAL,
                    ninki_snapshot INTEGER,
                    tansho_odds_snapshot REAL,
                    model_version TEXT,
                    license_check_passed INTEGER NOT NULL,
                    validator_passed INTEGER NOT NULL,
                    validator_notes TEXT,
                    created_at_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_predictions_race
                    ON predictions(race_date, track_code, race_number);
            ");
        }

        /// <summary>新規1行を追加する。既存行を書き換えるAPIは意図的に提供していない
        /// （クラスコメント参照）。</summary>
        public void Insert(PredictionRecord record)
        {
            Exec(@"
                INSERT INTO predictions
                    (prediction_id, race_date, track_code, race_number, umaban, ketto_num, horse_name, jockey_code,
                     category, content_text, reasons_json, ai_index_snapshot, ninki_snapshot,
                     tansho_odds_snapshot, model_version, license_check_passed, validator_passed,
                     validator_notes, created_at_utc)
                VALUES
                    (@id, @date, @track, @raceNum, @umaban, @ketto, @horseName, @jockeyCode,
                     @category, @text, @reasons, @index, @ninki,
                     @odds, @modelVer, @licenseOk, @validatorOk,
                     @notes, @created);
            ",
                p =>
                {
                    p.AddWithValue("@id", record.PredictionId);
                    p.AddWithValue("@date", record.RaceDate.ToString("yyyy-MM-dd"));
                    p.AddWithValue("@track", record.TrackCode);
                    p.AddWithValue("@raceNum", record.RaceNumber);
                    p.AddWithValue("@umaban", record.Umaban);
                    p.AddWithValue("@ketto", (object)record.KettoNum ?? DBNull.Value);
                    p.AddWithValue("@horseName", (object)record.HorseName ?? DBNull.Value);
                    p.AddWithValue("@jockeyCode", (object)record.JockeyCode ?? DBNull.Value);
                    p.AddWithValue("@category", record.Category);
                    p.AddWithValue("@text", record.ContentText);
                    p.AddWithValue("@reasons", JsonConvert.SerializeObject(record.Reasons));
                    p.AddWithValue("@index", (object)record.AiIndexSnapshot ?? DBNull.Value);
                    p.AddWithValue("@ninki", (object)record.NinkiSnapshot ?? DBNull.Value);
                    p.AddWithValue("@odds", (object)record.TanshoOddsSnapshot ?? DBNull.Value);
                    p.AddWithValue("@modelVer", (object)record.ModelVersion ?? DBNull.Value);
                    p.AddWithValue("@licenseOk", record.LicenseCheckPassed ? 1 : 0);
                    p.AddWithValue("@validatorOk", record.ValidatorPassed ? 1 : 0);
                    p.AddWithValue("@notes", (object)record.ValidatorNotes ?? DBNull.Value);
                    p.AddWithValue("@created", record.CreatedAtUtc.ToString("o"));
                });
        }

        /// <summary>ある開催場・日の予測（狙い馬/穴馬/危険な人気馬/AI指数TOP5すべて）を全件返す。
        /// 「latest」等での絞り込みは行わない。同じ馬が当日中に複数回予測対象になった場合、
        /// それぞれが別のprediction_idを持つ別行のままレース後検証（Issue #8）の対象になる
        /// （仕様書§18「不的中データも削除せず全件検証に含める」の対象を、再生成で増えた行も
        /// 含めて素直に全件とする解釈）。</summary>
        public System.Collections.Generic.List<PredictionRecord> GetForVenue(DateTime raceDate, string trackCode)
        {
            var result = new System.Collections.Generic.List<PredictionRecord>();
            using (var cmd = new SQLiteCommand(@"
                SELECT prediction_id, race_date, track_code, race_number, umaban, ketto_num, horse_name, jockey_code,
                       category, content_text, reasons_json, ai_index_snapshot, ninki_snapshot,
                       tansho_odds_snapshot, model_version, license_check_passed, validator_passed,
                       validator_notes, created_at_utc
                FROM predictions
                WHERE race_date=@date AND track_code=@track;", _conn))
            {
                cmd.Parameters.AddWithValue("@date", raceDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@track", trackCode);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        result.Add(new PredictionRecord
                        {
                            PredictionId = r.GetString(0),
                            RaceDate = DateTime.Parse(r.GetString(1)),
                            TrackCode = r.GetString(2),
                            RaceNumber = r.GetInt32(3),
                            Umaban = r.GetInt32(4),
                            KettoNum = r.IsDBNull(5) ? null : r.GetString(5),
                            HorseName = r.IsDBNull(6) ? null : r.GetString(6),
                            JockeyCode = r.IsDBNull(7) ? null : r.GetString(7),
                            Category = r.GetString(8),
                            ContentText = r.GetString(9),
                            Reasons = JsonConvert.DeserializeObject<System.Collections.Generic.List<string>>(r.GetString(10)) ?? new System.Collections.Generic.List<string>(),
                            AiIndexSnapshot = r.IsDBNull(11) ? (double?)null : r.GetDouble(11),
                            NinkiSnapshot = r.IsDBNull(12) ? (int?)null : r.GetInt32(12),
                            TanshoOddsSnapshot = r.IsDBNull(13) ? (double?)null : r.GetDouble(13),
                            ModelVersion = r.IsDBNull(14) ? null : r.GetString(14),
                            LicenseCheckPassed = r.GetInt32(15) != 0,
                            ValidatorPassed = r.GetInt32(16) != 0,
                            ValidatorNotes = r.IsDBNull(17) ? null : r.GetString(17),
                            CreatedAtUtc = DateTime.Parse(r.GetString(18)),
                        });
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
