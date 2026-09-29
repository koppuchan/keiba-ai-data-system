using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using KeibaDataCollector.Data;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using static KeibaDataCollector.Interop.JvDataSdk.JVData_Struct;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 仕様書§10「本日の傾向」を実装するTrend Engine。3段階（朝/開催中/終了後）で
    /// 脚質・枠・馬場・上がり・通過順を集計する。
    ///
    /// 朝段階の「過去データ」はHistoricalDataStore（開催場全体、全年・全距離を合算した粗い集計）
    /// から、「当日確定情報」（天候・馬場状態）は当日のRACEデータを直接読んで得る。
    /// 開催中・終了後段階は「当日結果」そのものが対象のため、historical.sqlite3を経由せず
    /// 当日のRACEデータ（RA+SE）を直接読んで都度集計する（FactorPublishServiceと同じ読み方）。
    ///
    /// 通過順傾向（最終コーナー先頭馬の勝率）の朝段階は、race_entries.final_corner_leader列
    /// （BackfillServiceが追加）を母集団にする。この列が無いDB・再backfill前の行は
    /// NULLのままなので、その分だけサンプル数が少なくなる（断定はMinSample未満なら行わない）。
    /// </summary>
    public class TrendEngineService
    {
        private readonly IRaceDataSource _source;
        private readonly HistoricalDataStore _historical;
        private readonly TrendStore _trendStore;

        // RaceCardService等と同じ理由（出走表は開催日より前に公開されるため）。
        private const string EarlyAnchorFromTime = "19860101000000";

        public TrendEngineService(IRaceDataSource source, HistoricalDataStore historical, TrendStore trendStore)
        {
            _source = source;
            _historical = historical;
            _trendStore = trendStore;
        }

        public VenueTrendSnapshot ComputeAndSave(DateTime raceDate, string trackCode, TrendStage stage)
        {
            var todayRaces = ReadTodayRaces(raceDate, trackCode);
            var finished = todayRaces.Where(r => r.IsFinished).ToList();

            var snapshot = new VenueTrendSnapshot
            {
                RaceDate = raceDate,
                TrackCode = trackCode,
                Stage = stage,
                ComputedAtUtc = DateTime.UtcNow,
                WeatherTrack = LatestWeather(todayRaces),
            };

            if (stage == TrendStage.Morning)
            {
                // 過去データ（開催場全体・全年合算）。
                snapshot.Pace = ComputeHistoricalPace(trackCode);
                snapshot.PostPosition = ComputeHistoricalPostPosition(trackCode);
                snapshot.Agari = ComputeHistoricalAgari(trackCode);
                snapshot.Passage = ComputeHistoricalPassage(trackCode);
                snapshot.RacesConsidered = CountHistoricalRaces(trackCode);
            }
            else
            {
                // 当日結果（開催中=ここまでに確定した分、終了後=全確定分。呼び出し側がタイミングを選ぶ）。
                snapshot.Pace = ComputeTodayPace(finished);
                snapshot.PostPosition = ComputeTodayPostPosition(finished);
                snapshot.Agari = ComputeTodayAgari(finished);
                snapshot.Passage = ComputeTodayPassage(finished);
                snapshot.RacesConsidered = finished.Count;
            }

            _trendStore.Save(snapshot);
            return snapshot;
        }

        // ---- 当日データの読み込み ----

        private class TodayHorse
        {
            public int Umaban;
            public int Waku;
            public int Chakujun;
            public double? Agari3F;
            public bool IsScratched;
        }

        private class TodayRace
        {
            public RaceKey Key;
            public string WeatherCode;
            public string TurfConditionCode;
            public string DirtConditionCode;
            public int[] EarliestCornerOrder = Array.Empty<int>();
            public int[] LatestCornerOrder = Array.Empty<int>();
            public List<TodayHorse> Horses = new List<TodayHorse>();

            /// <summary>「レースが確定したか」の代理指標。取消・除外を除く出走馬に
            /// 1件でも確定着順(chakujun>0)が付いていれば確定済みとみなす
            /// （JV-DataのSEレコードは、確定前はKakuteiJyuniが空/0のため）。</summary>
            public bool IsFinished => Horses.Any(h => !h.IsScratched && h.Chakujun > 0);
        }

        private List<TodayRace> ReadTodayRaces(DateTime targetDate, string trackCode)
        {
            var open = _source.Open("RACE", EarlyAnchorFromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode == -1)
            {
                _source.Close();
                return new List<TodayRace>();
            }
            if (open.ReturnCode < 0)
            {
                _source.Close();
                throw new InvalidOperationException($"{_source.SourceName} RACE Open failed: {open.ReturnCode}");
            }

            var races = new Dictionary<string, TodayRace>();

            try
            {
                while (true)
                {
                    int size = _source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue;
                    if (size == -3) { System.Threading.Thread.Sleep(500); continue; }
                    if (size < 0)
                        throw new InvalidOperationException($"{_source.SourceName} Read failed: {size}");

                    var typeId = JvRecordParser.GetRecordTypeId(buffer);

                    if (typeId == "RA")
                    {
                        var ra = new JV_RA_RACE();
                        ra.SetDataB(ref buffer);
                        var (raceKey, ok) = TryBuildRaceKey(ra.id.Year, ra.id.MonthDay, ra.id.JyoCD, ra.id.RaceNum, targetDate);
                        if (!ok || raceKey.TrackCode != trackCode) continue;

                        var slug = raceKey.AsSlug();
                        if (!races.TryGetValue(slug, out var race))
                        {
                            race = new TodayRace { Key = raceKey };
                            races[slug] = race;
                        }
                        race.WeatherCode = Trim(ra.TenkoBaba.TenkoCD);
                        race.TurfConditionCode = Trim(ra.TenkoBaba.SibaBabaCD);
                        race.DirtConditionCode = Trim(ra.TenkoBaba.DirtBabaCD);
                        race.EarliestCornerOrder = JvFactorRecordParser.ParseEarliestCornerOrder(ra);
                        race.LatestCornerOrder = JvFactorRecordParser.ParseLatestCornerOrder(ra);
                    }
                    else if (typeId == "SE")
                    {
                        var se = new JV_SE_RACE_UMA();
                        se.SetDataB(ref buffer);
                        var (raceKey, ok) = TryBuildRaceKey(se.id.Year, se.id.MonthDay, se.id.JyoCD, se.id.RaceNum, targetDate);
                        if (!ok || raceKey.TrackCode != trackCode) continue;

                        var slug = raceKey.AsSlug();
                        if (!races.TryGetValue(slug, out var race))
                        {
                            race = new TodayRace { Key = raceKey };
                            races[slug] = race;
                        }

                        var umaban = SafeInt(se.Umaban);
                        if (umaban <= 0) continue;

                        race.Horses.Add(new TodayHorse
                        {
                            Umaban = umaban,
                            Waku = SafeInt(se.Wakuban),
                            Chakujun = SafeInt(se.KakuteiJyuni),
                            Agari3F = SafeTenths(se.HaronTimeL3),
                            IsScratched = AiIndexService.IsScratchedCode(Trim(se.IJyoCD)),
                        });
                    }
                }
            }
            finally
            {
                _source.Close();
            }

            return races.Values.ToList();
        }

        private static WeatherTrackInfo LatestWeather(List<TodayRace> races)
        {
            // レース番号が最も進んでいる（＝直近発走に近い）記録を「現在の」天候・馬場状態とする。
            // 馬場状態は開催中に悪化/回復することがあるため、朝一番の値のままにしない。
            var latest = races.OrderByDescending(r => r.Key.RaceNumber).FirstOrDefault();
            if (latest == null) return new WeatherTrackInfo();
            return new WeatherTrackInfo
            {
                WeatherCode = latest.WeatherCode,
                TurfConditionCode = latest.TurfConditionCode,
                DirtConditionCode = latest.DirtConditionCode,
            };
        }

        // ---- 当日結果からの集計（開催中/終了後） ----

        private static PaceTendency ComputeTodayPace(List<TodayRace> races)
        {
            var placed = new List<double>();
            var rest = new List<double>();

            foreach (var race in races)
            {
                if (race.EarliestCornerOrder.Length <= 1) continue;
                foreach (var h in race.Horses)
                {
                    if (h.IsScratched || h.Chakujun <= 0) continue;
                    var ratio = EarlyPositionRatio(race.EarliestCornerOrder, h.Umaban);
                    if (!ratio.HasValue) continue;
                    if (h.Chakujun <= 3) placed.Add(ratio.Value); else rest.Add(ratio.Value);
                }
            }

            var tendency = new PaceTendency { SampleCount = placed.Count + rest.Count };
            if (placed.Count == 0 || rest.Count == 0) return tendency;

            tendency.PlacedAvgEarlyPositionRatio = placed.Average();
            tendency.RestAvgEarlyPositionRatio = rest.Average();
            if (tendency.HasEnoughSample)
                tendency.FrontRunnerFavored = tendency.PlacedAvgEarlyPositionRatio < tendency.RestAvgEarlyPositionRatio;
            return tendency;
        }

        private static PostPositionTendency ComputeTodayPostPosition(List<TodayRace> races)
        {
            var byWaku = new Dictionary<int, (int Total, int Rentai)>();
            foreach (var race in races)
            {
                foreach (var h in race.Horses)
                {
                    if (h.IsScratched || h.Chakujun <= 0 || h.Waku < 1 || h.Waku > 8) continue;
                    var (total, rentai) = byWaku.TryGetValue(h.Waku, out var v) ? v : (0, 0);
                    total++;
                    if (h.Chakujun <= 2) rentai++;
                    byWaku[h.Waku] = (total, rentai);
                }
            }

            var tendency = new PostPositionTendency();
            foreach (var kv in byWaku)
            {
                tendency.ByWaku[kv.Key] = new WakuStat
                {
                    SampleCount = kv.Value.Total,
                    PlaceRate = kv.Value.Total >= PostPositionTendency.MinSample
                        ? (double?)kv.Value.Rentai / kv.Value.Total
                        : null,
                };
            }
            return tendency;
        }

        private static AgariTendency ComputeTodayAgari(List<TodayRace> races)
        {
            var values = races.SelectMany(r => r.Horses)
                .Where(h => !h.IsScratched && h.Chakujun > 0 && h.Agari3F.HasValue)
                .Select(h => h.Agari3F.Value)
                .ToList();

            return new AgariTendency
            {
                SampleCount = values.Count,
                AverageAgari3F = values.Count > 0 ? values.Average() : (double?)null,
            };
        }

        private static PassageTendency ComputeTodayPassage(List<TodayRace> races)
        {
            int wins = 0, sample = 0;
            foreach (var race in races)
            {
                if (race.LatestCornerOrder.Length == 0) continue;
                var leaderUmaban = race.LatestCornerOrder[0];
                var leader = race.Horses.FirstOrDefault(h => h.Umaban == leaderUmaban && !h.IsScratched && h.Chakujun > 0);
                if (leader == null) continue;

                sample++;
                if (leader.Chakujun == 1) wins++;
            }

            var tendency = new PassageTendency { SampleCount = sample };
            if (sample >= PassageTendency.MinSample)
                tendency.LeaderWinRate = (double)wins / sample;
            return tendency;
        }

        // ---- 過去データからの集計（朝） ----

        private PaceTendency ComputeHistoricalPace(string trackCode)
        {
            double? placedAvg = null, restAvg = null;
            int placedCount = 0, restCount = 0;
            using (var cmd = new SQLiteCommand(@"
                SELECT
                    AVG(CASE WHEN chakujun BETWEEN 1 AND 3 THEN early_position_ratio END) placed_avg,
                    SUM(CASE WHEN chakujun BETWEEN 1 AND 3 THEN 1 ELSE 0 END) placed_count,
                    AVG(CASE WHEN chakujun > 3 THEN early_position_ratio END) rest_avg,
                    SUM(CASE WHEN chakujun > 3 THEN 1 ELSE 0 END) rest_count
                FROM race_entries
                WHERE track_code=@track AND chakujun > 0 AND early_position_ratio IS NOT NULL;", _historical.Connection))
            {
                cmd.Parameters.AddWithValue("@track", trackCode);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        // WHERE条件に合致する行が0件だとSUM/AVGはNULLを返す
                        // （FactorScoringService.ComputePaceScoreと同じ注意点。同ファイルの
                        // コメント参照: GROUP BY無しの集約は対象0行でも1行返り、その値がNULLになる）。
                        if (!r.IsDBNull(0)) placedAvg = r.GetDouble(0);
                        if (!r.IsDBNull(1)) placedCount = r.GetInt32(1);
                        if (!r.IsDBNull(2)) restAvg = r.GetDouble(2);
                        if (!r.IsDBNull(3)) restCount = r.GetInt32(3);
                    }
                }
            }

            var tendency = new PaceTendency { SampleCount = placedCount + restCount };
            if (placedCount == 0 || restCount == 0) return tendency;

            tendency.PlacedAvgEarlyPositionRatio = placedAvg;
            tendency.RestAvgEarlyPositionRatio = restAvg;
            if (tendency.HasEnoughSample && placedAvg.HasValue && restAvg.HasValue)
                tendency.FrontRunnerFavored = placedAvg.Value < restAvg.Value;
            return tendency;
        }

        private PostPositionTendency ComputeHistoricalPostPosition(string trackCode)
        {
            var tendency = new PostPositionTendency();
            using (var cmd = new SQLiteCommand(@"
                SELECT waku, COUNT(*) total, SUM(CASE WHEN chakujun BETWEEN 1 AND 2 THEN 1 ELSE 0 END) rentai
                FROM race_entries
                WHERE track_code=@track AND chakujun > 0 AND waku BETWEEN 1 AND 8
                GROUP BY waku;", _historical.Connection))
            {
                cmd.Parameters.AddWithValue("@track", trackCode);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var total = r.GetInt64(1);
                        var rentai = r.GetInt64(2);
                        tendency.ByWaku[r.GetInt32(0)] = new WakuStat
                        {
                            SampleCount = (int)total,
                            PlaceRate = total >= PostPositionTendency.MinSample ? (double?)rentai / total : null,
                        };
                    }
                }
            }
            return tendency;
        }

        private AgariTendency ComputeHistoricalAgari(string trackCode)
        {
            using (var cmd = new SQLiteCommand(
                "SELECT COUNT(*), AVG(agari_3f) FROM race_entries " +
                "WHERE track_code=@track AND chakujun > 0 AND agari_3f IS NOT NULL AND agari_3f > 0;", _historical.Connection))
            {
                cmd.Parameters.AddWithValue("@track", trackCode);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read() && !r.IsDBNull(0))
                    {
                        var count = (int)r.GetInt64(0);
                        return new AgariTendency
                        {
                            SampleCount = count,
                            AverageAgari3F = count > 0 && !r.IsDBNull(1) ? r.GetDouble(1) : (double?)null,
                        };
                    }
                }
            }
            return new AgariTendency();
        }

        /// <summary>朝段階の通過順傾向（最終コーナー先頭馬の勝率）。BackfillServiceが
        /// race_entries.final_corner_leaderへ保存する実績データを母集団にする。
        /// 旧DB・再backfill前の行はNULLのため、AND final_corner_leader IS NOT NULLで自然に除外される
        /// （0件扱いではなく「その行は判定不能」として単に集計対象から外れる）。</summary>
        private PassageTendency ComputeHistoricalPassage(string trackCode)
        {
            using (var cmd = new SQLiteCommand(@"
                SELECT COUNT(*), SUM(CASE WHEN chakujun = 1 THEN 1 ELSE 0 END)
                FROM race_entries
                WHERE track_code=@track AND chakujun > 0 AND final_corner_leader = 1;", _historical.Connection))
            {
                cmd.Parameters.AddWithValue("@track", trackCode);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read() && !r.IsDBNull(0))
                    {
                        var sample = (int)r.GetInt64(0);
                        var wins = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                        var tendency = new PassageTendency { SampleCount = sample };
                        if (sample >= PassageTendency.MinSample)
                            tendency.LeaderWinRate = (double)wins / sample;
                        return tendency;
                    }
                }
            }
            return new PassageTendency();
        }

        private int CountHistoricalRaces(string trackCode)
        {
            using (var cmd = new SQLiteCommand(
                "SELECT COUNT(DISTINCT race_date || '-' || race_number) FROM race_entries WHERE track_code=@track;", _historical.Connection))
            {
                cmd.Parameters.AddWithValue("@track", trackCode);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
            }
        }

        // ---- 共通ヘルパー ----

        /// <summary>BackfillService.ComputeEarlyPositionRatioと同じ定義（0=先頭通過、1=最後尾通過）。
        /// 当日データはhistorical.sqlite3を経由せずその場で読むため、同じ計算をここでも行う
        /// （5行程度の純粋関数のため、モジュール間の依存を増やすよりこちらを選んだ）。</summary>
        private static double? EarlyPositionRatio(int[] earliestCornerOrder, int umaban)
        {
            if (earliestCornerOrder == null || earliestCornerOrder.Length <= 1 || umaban <= 0) return null;
            var index = Array.IndexOf(earliestCornerOrder, umaban);
            if (index < 0) return null;
            return (double)index / (earliestCornerOrder.Length - 1);
        }

        private static (RaceKey Key, bool Ok) TryBuildRaceKey(string year, string monthDay, string jyoCd, string raceNum, DateTime targetDate)
        {
            var m = Trim(monthDay);
            var month = m.Length >= 2 ? SafeInt(m.Substring(0, 2)) : 0;
            var day = m.Length >= 4 ? SafeInt(m.Substring(2, 2)) : 0;
            var y = SafeInt(year);

            DateTime date;
            try
            {
                date = y > 0 && month > 0 && day > 0 ? new DateTime(y, month, day) : DateTime.MinValue;
            }
            catch (ArgumentOutOfRangeException)
            {
                return (null, false);
            }
            if (date.Date != targetDate.Date) return (null, false);

            return (new RaceKey { TrackCode = Trim(jyoCd), RaceDate = date, RaceNumber = SafeInt(raceNum) }, true);
        }

        private static double? SafeTenths(string s)
        {
            var t = Trim(s);
            if (t.Length == 0 || !int.TryParse(t, out var v) || v == 0) return null;
            return v / 10.0;
        }

        private static int SafeInt(string s)
        {
            var t = Trim(s);
            return int.TryParse(t, out var v) ? v : 0;
        }

        private static string Trim(string s) => (s ?? string.Empty).Trim();
    }
}
