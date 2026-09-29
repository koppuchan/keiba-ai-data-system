using System;
using System.Data.SQLite;
using KeibaDataCollector.Models;
using Newtonsoft.Json;

namespace KeibaDataCollector.Data
{
    /// <summary>
    /// 仕様書§10「本日の傾向」の永続化層。
    ///
    /// 1開催場・1日・1段階（朝/開催中/終了後）につき1行。開催中の段階は当日中に何度も
    /// 再計算されるが、「現時点の傾向」という単一の最新値を表示できれば十分なため、
    /// 再計算のたびに上書きする（全計算履歴を残す設計にはしていない）。
    ///
    /// スナップショットの内訳（脚質・枠・馬場・上がり・通過順の各集計値）はJSON文字列として
    /// 保存する。仕様書はこの内訳の型を厳密に定めておらず、将来的に集計軸が増減する可能性が
    /// あるため、検索に使う列（race_date/track_code/stage/races_considered）だけを個別カラムにし、
    /// 内容そのものはJSONへ逃がす設計にした（race_card等、既存システムが表示用データをWordPress
    /// メタへJSON文字列で保存しているのと同じ考え方）。
    /// </summary>
    public class TrendStore : IDisposable
    {
        private readonly SQLiteConnection _conn;
        private readonly bool _ownsConnection;

        public TrendStore(string dbPath)
        {
            var dir = System.IO.Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            _conn = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            _conn.Open();
            _ownsConnection = true;
            EnsureSchema();
        }

        public TrendStore(SQLiteConnection existingConnection)
        {
            _conn = existingConnection;
            _ownsConnection = false;
            EnsureSchema();
        }

        private void EnsureSchema()
        {
            Exec(@"
                CREATE TABLE IF NOT EXISTS trend_snapshots (
                    race_date TEXT NOT NULL,
                    track_code TEXT NOT NULL,
                    stage TEXT NOT NULL,
                    computed_at_utc TEXT NOT NULL,
                    races_considered INTEGER NOT NULL,
                    snapshot_json TEXT NOT NULL,
                    PRIMARY KEY (race_date, track_code, stage)
                );
            ");
        }

        public void Save(VenueTrendSnapshot snapshot)
        {
            var json = JsonConvert.SerializeObject(snapshot);
            Exec(@"
                INSERT INTO trend_snapshots (race_date, track_code, stage, computed_at_utc, races_considered, snapshot_json)
                VALUES (@date, @track, @stage, @computed, @races, @json)
                ON CONFLICT(race_date, track_code, stage) DO UPDATE SET
                    computed_at_utc=excluded.computed_at_utc,
                    races_considered=excluded.races_considered,
                    snapshot_json=excluded.snapshot_json;
            ",
                p =>
                {
                    p.AddWithValue("@date", snapshot.RaceDate.ToString("yyyy-MM-dd"));
                    p.AddWithValue("@track", snapshot.TrackCode);
                    p.AddWithValue("@stage", snapshot.Stage.ToString());
                    p.AddWithValue("@computed", snapshot.ComputedAtUtc.ToString("o"));
                    p.AddWithValue("@races", snapshot.RacesConsidered);
                    p.AddWithValue("@json", json);
                });
        }

        public VenueTrendSnapshot Get(DateTime raceDate, string trackCode, TrendStage stage)
        {
            using (var cmd = new SQLiteCommand(
                "SELECT snapshot_json FROM trend_snapshots WHERE race_date=@date AND track_code=@track AND stage=@stage;", _conn))
            {
                cmd.Parameters.AddWithValue("@date", raceDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@track", trackCode);
                cmd.Parameters.AddWithValue("@stage", stage.ToString());
                var result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value) return null;
                return JsonConvert.DeserializeObject<VenueTrendSnapshot>((string)result);
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
            if (_ownsConnection)
            {
                _conn?.Close();
                _conn?.Dispose();
            }
        }
    }
}
