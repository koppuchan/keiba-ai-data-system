using System;
using System.Collections.Generic;
using System.Data.SQLite;
using KeibaDataCollector.Models;

namespace KeibaDataCollector.Data
{
    /// <summary>
    /// 仕様書§8・§9 AI指数の永続化層。
    ///
    /// - score_weights: 6ファクターの重み。セグメント（中央/地方 × 芝/ダート）ごとに別設定可能。
    ///   行が無いセグメントは "default" セグメントにフォールバックし、それも無ければ全項目1.0
    ///   （単純平均相当）を使う。
    /// - scores: 直近の算出結果（1頭1レース1行。再計算のたびに上書き）。model_version・
    ///   feature_version・data_cutoff_utcを必ず保存する（仕様書§8）。
    ///
    /// 「予測時点のデータをimmutableに残す」こと自体は仕様書§18 Verification DBの責務
    /// （別テーブルpredictionsに実装）。このテーブルは「現時点の最新指数」を
    /// 引き直すためのキャッシュという位置づけで、再計算のたびに上書きしてよい。
    /// </summary>
    public class ScoresStore : IDisposable
    {
        public const string DefaultSegment = "default";

        private readonly SQLiteConnection _conn;
        private readonly bool _ownsConnection;

        public ScoresStore(string dbPath)
        {
            _conn = SqliteConnections.Open(dbPath);
            _ownsConnection = true;
            EnsureSchema();
        }

        /// <summary>HistoricalDataStore等、既に開いているコネクションに相乗りする場合用。</summary>
        public ScoresStore(SQLiteConnection existingConnection)
        {
            _conn = existingConnection;
            _ownsConnection = false;
            EnsureSchema();
        }

        private void EnsureSchema()
        {
            Exec(@"
                CREATE TABLE IF NOT EXISTS score_weights (
                    segment TEXT PRIMARY KEY,
                    weight_bias REAL NOT NULL DEFAULT 1.0,
                    weight_pace REAL NOT NULL DEFAULT 1.0,
                    weight_agari_q REAL NOT NULL DEFAULT 1.0,
                    weight_jockey_roi REAL NOT NULL DEFAULT 1.0,
                    weight_pedigree_fit REAL NOT NULL DEFAULT 1.0,
                    weight_training_acc REAL NOT NULL DEFAULT 1.0,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS scores (
                    race_date TEXT NOT NULL,
                    track_code TEXT NOT NULL,
                    race_number INTEGER NOT NULL,
                    umaban INTEGER NOT NULL,
                    ketto_num TEXT,
                    horse_name TEXT,
                    jockey_code TEXT,
                    baba_condition_code TEXT,
                    param_bias REAL,
                    param_pace REAL,
                    param_agari_q REAL,
                    param_jockey_roi REAL,
                    param_pedigree_fit REAL,
                    param_training_acc REAL,
                    ai_index REAL,
                    data_completeness REAL NOT NULL,
                    is_scratched INTEGER NOT NULL DEFAULT 0,
                    model_version TEXT NOT NULL,
                    feature_version TEXT NOT NULL,
                    data_cutoff_utc TEXT NOT NULL,
                    computed_at_utc TEXT NOT NULL,
                    PRIMARY KEY (race_date, track_code, race_number, umaban)
                );
                CREATE INDEX IF NOT EXISTS idx_scores_venue_rank
                    ON scores(race_date, track_code, is_scratched, ai_index);
            ");
        }

        // ---- 重み ----

        /// <summary>セグメント（例: "central:turf"）の重みを取得する。無ければ"default"、
        /// それも無ければ全項目1.0（単純平均相当）を返す。</summary>
        public AiIndexWeights GetWeights(string segment)
        {
            var found = GetWeightsRow(segment);
            if (found != null) return found;

            var fallback = GetWeightsRow(DefaultSegment);
            if (fallback != null) return fallback;

            return new AiIndexWeights { Segment = segment };
        }

        private AiIndexWeights GetWeightsRow(string segment)
        {
            using (var cmd = new SQLiteCommand(
                "SELECT segment, weight_bias, weight_pace, weight_agari_q, weight_jockey_roi, " +
                "weight_pedigree_fit, weight_training_acc, updated_at_utc FROM score_weights WHERE segment=@segment;", _conn))
            {
                cmd.Parameters.AddWithValue("@segment", segment);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return new AiIndexWeights
                    {
                        Segment = r.GetString(0),
                        WeightBias = r.GetDouble(1),
                        WeightPace = r.GetDouble(2),
                        WeightAgariQ = r.GetDouble(3),
                        WeightJockeyRoi = r.GetDouble(4),
                        WeightPedigreeFit = r.GetDouble(5),
                        WeightTrainingAcc = r.GetDouble(6),
                        UpdatedAtUtc = DateTime.Parse(r.GetString(7)),
                    };
                }
            }
        }

        public void SetWeights(AiIndexWeights w)
        {
            if (string.IsNullOrEmpty(w.Segment))
                throw new ArgumentException("Segment は必須です。", nameof(w));

            Exec(@"
                INSERT INTO score_weights
                    (segment, weight_bias, weight_pace, weight_agari_q, weight_jockey_roi, weight_pedigree_fit, weight_training_acc, updated_at_utc)
                VALUES (@segment, @bias, @pace, @agari, @jockey, @pedigree, @training, @updated)
                ON CONFLICT(segment) DO UPDATE SET
                    weight_bias=excluded.weight_bias,
                    weight_pace=excluded.weight_pace,
                    weight_agari_q=excluded.weight_agari_q,
                    weight_jockey_roi=excluded.weight_jockey_roi,
                    weight_pedigree_fit=excluded.weight_pedigree_fit,
                    weight_training_acc=excluded.weight_training_acc,
                    updated_at_utc=excluded.updated_at_utc;
            ",
                p =>
                {
                    p.AddWithValue("@segment", w.Segment);
                    p.AddWithValue("@bias", w.WeightBias);
                    p.AddWithValue("@pace", w.WeightPace);
                    p.AddWithValue("@agari", w.WeightAgariQ);
                    p.AddWithValue("@jockey", w.WeightJockeyRoi);
                    p.AddWithValue("@pedigree", w.WeightPedigreeFit);
                    p.AddWithValue("@training", w.WeightTrainingAcc);
                    p.AddWithValue("@updated", DateTime.UtcNow.ToString("o"));
                });
        }

        // ---- スコア ----

        public void UpsertScore(AiIndexResult s)
        {
            Exec(@"
                INSERT INTO scores
                    (race_date, track_code, race_number, umaban, ketto_num, horse_name, jockey_code, baba_condition_code,
                     param_bias, param_pace, param_agari_q, param_jockey_roi, param_pedigree_fit, param_training_acc,
                     ai_index, data_completeness, is_scratched, model_version, feature_version, data_cutoff_utc, computed_at_utc)
                VALUES
                    (@date, @track, @raceNum, @umaban, @ketto, @horseName, @jockeyCode, @babaCondition,
                     @bias, @pace, @agari, @jockey, @pedigree, @training,
                     @index, @completeness, @scratched, @modelVer, @featureVer, @cutoff, @computed)
                ON CONFLICT(race_date, track_code, race_number, umaban) DO UPDATE SET
                    ketto_num=excluded.ketto_num,
                    horse_name=excluded.horse_name,
                    jockey_code=excluded.jockey_code,
                    baba_condition_code=excluded.baba_condition_code,
                    param_bias=excluded.param_bias,
                    param_pace=excluded.param_pace,
                    param_agari_q=excluded.param_agari_q,
                    param_jockey_roi=excluded.param_jockey_roi,
                    param_pedigree_fit=excluded.param_pedigree_fit,
                    param_training_acc=excluded.param_training_acc,
                    ai_index=excluded.ai_index,
                    data_completeness=excluded.data_completeness,
                    is_scratched=excluded.is_scratched,
                    model_version=excluded.model_version,
                    feature_version=excluded.feature_version,
                    data_cutoff_utc=excluded.data_cutoff_utc,
                    computed_at_utc=excluded.computed_at_utc;
            ",
                p =>
                {
                    p.AddWithValue("@date", s.RaceDate.ToString("yyyy-MM-dd"));
                    p.AddWithValue("@track", s.TrackCode);
                    p.AddWithValue("@raceNum", s.RaceNumber);
                    p.AddWithValue("@umaban", s.Umaban);
                    p.AddWithValue("@ketto", (object)s.KettoNum ?? DBNull.Value);
                    p.AddWithValue("@horseName", (object)s.HorseName ?? DBNull.Value);
                    p.AddWithValue("@jockeyCode", (object)s.JockeyCode ?? DBNull.Value);
                    p.AddWithValue("@babaCondition", (object)s.BabaConditionCode ?? DBNull.Value);
                    p.AddWithValue("@bias", (object)s.Factors?.ParamBias ?? DBNull.Value);
                    p.AddWithValue("@pace", (object)s.Factors?.ParamPace ?? DBNull.Value);
                    p.AddWithValue("@agari", (object)s.Factors?.ParamAgariQ ?? DBNull.Value);
                    p.AddWithValue("@jockey", (object)s.Factors?.ParamJockeyRoi ?? DBNull.Value);
                    p.AddWithValue("@pedigree", (object)s.Factors?.ParamPedigreeFit ?? DBNull.Value);
                    p.AddWithValue("@training", (object)s.Factors?.ParamTrainingAcc ?? DBNull.Value);
                    p.AddWithValue("@index", (object)s.AiIndex ?? DBNull.Value);
                    p.AddWithValue("@completeness", s.DataCompleteness);
                    p.AddWithValue("@scratched", s.IsScratched ? 1 : 0);
                    p.AddWithValue("@modelVer", s.ModelVersion);
                    p.AddWithValue("@featureVer", s.FeatureVersion);
                    p.AddWithValue("@cutoff", s.DataCutoffUtc.ToString("o"));
                    p.AddWithValue("@computed", s.ComputedAtUtc.ToString("o"));
                });
        }

        /// <summary>1レース分の全馬のスコアを取得する（取消・除外馬も含む。除外はContentGenerator/Validator
        /// 側の責務）。Content Generator（仕様書§11）が狙い馬・穴馬・危険な人気馬をレース単位で
        /// 判定する際の入力になる。</summary>
        public List<AiIndexResult> GetRaceScores(DateTime raceDate, string trackCode, int raceNumber)
        {
            var result = new List<AiIndexResult>();
            using (var cmd = new SQLiteCommand(@"
                SELECT race_date, track_code, race_number, umaban, ketto_num, horse_name, jockey_code, baba_condition_code,
                       param_bias, param_pace, param_agari_q, param_jockey_roi, param_pedigree_fit, param_training_acc,
                       ai_index, data_completeness, is_scratched, model_version, feature_version, data_cutoff_utc, computed_at_utc
                FROM scores
                WHERE race_date=@date AND track_code=@track AND race_number=@raceNum
                ORDER BY umaban ASC;", _conn))
            {
                cmd.Parameters.AddWithValue("@date", raceDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@track", trackCode);
                cmd.Parameters.AddWithValue("@raceNum", raceNumber);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        result.Add(ReadRow(r));
                }
            }
            return result;
        }

        /// <summary>仕様書§9 AI指数TOP5。開催場（race_date×track_code）全体で、取消・除外馬を除き、
        /// ai_index降順で上位5頭を返す。同点はデータ充足率降順→馬番昇順でタイブレークする
        /// （馬番は決定的な最終タイブレーク。仕様書は「データ充足率・信頼度等」と例示するのみで
        /// 一意な基準を定めていないため、再現性のために最後に馬番で確定させる）。</summary>
        public List<AiIndexResult> GetVenueTop5(DateTime raceDate, string trackCode)
        {
            var result = new List<AiIndexResult>();
            using (var cmd = new SQLiteCommand(@"
                SELECT race_date, track_code, race_number, umaban, ketto_num, horse_name, jockey_code, baba_condition_code,
                       param_bias, param_pace, param_agari_q, param_jockey_roi, param_pedigree_fit, param_training_acc,
                       ai_index, data_completeness, is_scratched, model_version, feature_version, data_cutoff_utc, computed_at_utc
                FROM scores
                WHERE race_date=@date AND track_code=@track AND is_scratched=0 AND ai_index IS NOT NULL
                ORDER BY ai_index DESC, data_completeness DESC, umaban ASC
                LIMIT 5;", _conn))
            {
                cmd.Parameters.AddWithValue("@date", raceDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@track", trackCode);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        result.Add(ReadRow(r));
                }
            }
            return result;
        }

        private static AiIndexResult ReadRow(SQLiteDataReader r)
        {
            return new AiIndexResult
            {
                RaceDate = DateTime.Parse(r.GetString(0)),
                TrackCode = r.GetString(1),
                RaceNumber = r.GetInt32(2),
                Umaban = r.GetInt32(3),
                KettoNum = r.IsDBNull(4) ? null : r.GetString(4),
                HorseName = r.IsDBNull(5) ? null : r.GetString(5),
                JockeyCode = r.IsDBNull(6) ? null : r.GetString(6),
                BabaConditionCode = r.IsDBNull(7) ? null : r.GetString(7),
                Factors = new FactorScores
                {
                    ParamBias = r.IsDBNull(8) ? (double?)null : r.GetDouble(8),
                    ParamPace = r.IsDBNull(9) ? (double?)null : r.GetDouble(9),
                    ParamAgariQ = r.IsDBNull(10) ? (double?)null : r.GetDouble(10),
                    ParamJockeyRoi = r.IsDBNull(11) ? (double?)null : r.GetDouble(11),
                    ParamPedigreeFit = r.IsDBNull(12) ? (double?)null : r.GetDouble(12),
                    ParamTrainingAcc = r.IsDBNull(13) ? (double?)null : r.GetDouble(13),
                },
                AiIndex = r.IsDBNull(14) ? (double?)null : r.GetDouble(14),
                DataCompleteness = r.GetDouble(15),
                IsScratched = r.GetInt32(16) != 0,
                ModelVersion = r.GetString(17),
                FeatureVersion = r.GetString(18),
                DataCutoffUtc = DateTime.Parse(r.GetString(19)),
                ComputedAtUtc = DateTime.Parse(r.GetString(20)),
            };
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
