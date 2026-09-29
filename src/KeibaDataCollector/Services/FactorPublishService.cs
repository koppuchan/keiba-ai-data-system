using System;
using System.Collections.Generic;
using System.Linq;
using KeibaDataCollector.Data;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using KeibaDataCollector.WordPress;
using static KeibaDataCollector.Interop.JvDataSdk.JVData_Struct;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 当日の出走馬について6ファクター＋統合AI指数（仕様書§8・§9）を算出し、
    /// WordPressのhrc_factorsへ送信するとともに、AI指数はscoresテーブルへ永続化する。
    ///
    /// WordPress側の race_card（RaceCardService経由で既に送信済み）には血統登録番号(KettoNum)が
    /// 含まれていない（表示に不要なため元々持たせていない）。そのため、ここではWordPress経由ではなく
    /// RaceCardServiceと同じ"RACE"データ種別を当日分だけ直接開き、SEレコードからKettoNumを
    /// 取り出してHistoricalDataStoreと突き合わせる。
    ///
    /// FactorScoringServiceが返すスコアはローカルSQLiteの蓄積状況に依存する。血統(⑤)がまだ
    /// 0件（BLOD取得の問題が未解決）の間は、⑤は全馬nullのまま送信される
    /// （nullのフィールドはJSON自体に含めない。WordPress側は欠けたキーとして扱える）。
    /// </summary>
    public class FactorPublishService
    {
        private readonly IRaceDataSource _source;
        private readonly WordPressClient _wp;
        private readonly AiIndexService _aiIndex;
        private readonly ScoresStore _scoresStore;

        // RaceCardServiceと同じ理由（出馬表は開催日より前に公開されるため）。
        private const string EarlyAnchorFromTime = "19860101000000";

        /// <summary>SourceNameに"中央"を含むかどうかでJV-Link/UmaConnを判別する。
        /// IRaceDataSourceインターフェース自体にIsCentral相当のプロパティを追加すると
        /// Issue #2で既にレビュー中のインターフェースを変更することになるため、
        /// 既存の公開プロパティ（SourceName）から導出する非侵襲的な方法を選んだ。</summary>
        private bool IsCentral => _source.SourceName.Contains("中央");

        public FactorPublishService(IRaceDataSource source, WordPressClient wp, FactorScoringService scoring, ScoresStore scoresStore)
        {
            _source = source;
            _wp = wp;
            _scoresStore = scoresStore;
            _aiIndex = new AiIndexService(scoring, scoresStore);
        }

        public void RunForToday(DateTime targetDate)
        {
            // この実行で読んだデータの基準時刻。同一実行内の全馬で揃える
            // （仕様書§8 data_cutoffの粒度は「この算出バッチが何時点のデータを見たか」で十分なため、
            // 馬ごとに個別のタイムスタンプを持たせる必要はない）。
            var dataCutoffUtc = DateTime.UtcNow;

            var open = _source.Open("RACE", EarlyAnchorFromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode == -1)
            {
                _source.Close();
                Console.WriteLine($"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 該当データなし（開催が無い等）。");
                return;
            }
            if (open.ReturnCode < 0)
            {
                _source.Close();
                throw new InvalidOperationException($"{_source.SourceName} RACE Open failed: {open.ReturnCode}");
            }

            // レースキー(slug)ごとの距離・トラック種別。RA到着時に埋め、SE処理時に参照する
            // （BackfillServiceのBackfillRaceEntriesと同じ、RA→SEの到着順を前提にした組み方）。
            var raceInfoByKey = new Dictionary<string, (int Distance, string TrackSurfaceCode)>();
            var entriesByRace = new Dictionary<string, List<(int Umaban, FactorScoringInput Input)>>();
            var raceKeys = new Dictionary<string, RaceKey>();

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
                        if (!ok) continue;
                        raceInfoByKey[raceKey.AsSlug()] = (SafeInt(ra.Kyori), Trim(ra.TrackCD));
                    }
                    else if (typeId == "SE")
                    {
                        var se = new JV_SE_RACE_UMA();
                        se.SetDataB(ref buffer);
                        var (raceKey, ok) = TryBuildRaceKey(se.id.Year, se.id.MonthDay, se.id.JyoCD, se.id.RaceNum, targetDate);
                        if (!ok) continue;

                        var slug = raceKey.AsSlug();
                        if (!raceInfoByKey.TryGetValue(slug, out var raceInfo))
                            continue; // 距離が分からない馬は集計不能なのでスキップ。

                        var kettoNum = Trim(se.KettoNum);
                        var umaban = SafeInt(se.Umaban);
                        if (string.IsNullOrEmpty(kettoNum) || umaban <= 0) continue;

                        var input = new FactorScoringInput
                        {
                            KettoNum = kettoNum,
                            TrackCode = raceKey.TrackCode,
                            Distance = raceInfo.Distance,
                            TrackSurfaceCode = raceInfo.TrackSurfaceCode,
                            Waku = SafeInt(se.Wakuban),
                            JockeyCode = Trim(se.KisyuCode),
                            IJyoCd = Trim(se.IJyoCD),
                        };

                        if (!entriesByRace.TryGetValue(slug, out var list))
                        {
                            list = new List<(int, FactorScoringInput)>();
                            entriesByRace[slug] = list;
                            raceKeys[slug] = raceKey;
                        }
                        list.Add((umaban, input));
                    }
                }
            }
            finally
            {
                _source.Close();
            }

            int published = 0, skipped = 0, failed = 0;
            foreach (var slug in entriesByRace.Keys)
            {
                var raceKey = raceKeys[slug];

                // スコア計算自体も1レース単位で保護する。以前はここが素通しで、
                // FactorScoringService内の未知の例外（実機で発生: 特定コース条件の
                // 母集団が0件になりSUM集計がNULLを返してInvalidCastExceptionになった
                // ケース）が起きると、その日のこのソースの残り全レースが処理されずに
                // 巻き添えで終了していた（中京が丸ごと・新潟の一部が欠けた原因）。
                // 直接の原因はFactorScoringService側で個別に直したが、同じ壊れ方を
                // 二度としないよう、ここでもレース単位に隔離しておく。
                var scores = new Dictionary<int, FactorScores>();
                try
                {
                    foreach (var (umaban, input) in entriesByRace[slug])
                    {
                        // AiIndexServiceが内部でFactorScoringService.Computeを1回だけ呼び、
                        // 6ファクター（hrc_factors送信用）とAI指数（scores永続化用）の両方を
                        // 同じ計算結果から作る（二重計算を避ける）。
                        var result = _aiIndex.ComputeAndPersist(
                            input, targetDate, raceKey.RaceNumber, umaban, IsCentral, dataCutoffUtc);
                        scores[umaban] = result.Factors;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine(
                        $"[{_source.SourceName}] {slug} 6ファクター/AI指数の計算に失敗（このレースのみスキップして続行）: {ex.Message}");
                    continue;
                }

                // 1レースの送信失敗で、残りのレースまで巻き添えにしない。
                // 既存システムの朝一バッチはここで例外を上まで投げてしまい、WordPressが
                // 503を1回返しただけで、その後の全レースの出走表が作られないまま
                // 異常終了していた（笠松が丸ごと欠けた原因）。同じ壊れ方をしないよう、
                // レース単位で捕まえて次へ進む。WordPressClient側でも再送はするので、
                // ここまで来るのは再送しても駄目だった場合だけ。
                bool applied;
                try
                {
                    applied = _wp.UpsertFactorsAsync(raceKey, scores).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine(
                        $"[{_source.SourceName}] {slug} 6ファクターの反映に失敗（このレースのみスキップして続行）: {ex.Message}");
                    continue;
                }

                if (!applied)
                {
                    // 出走表がまだWordPressに無いレース。ここで投稿を作ると馬名の無い
                    // 空のレースができてしまうため送信しない（WordPressClient側のコメント参照）。
                    skipped++;
                    Console.WriteLine(
                        $"[{_source.SourceName}] {slug} 出走表がWordPressにまだ無いため6ファクターの反映を見送りました" +
                        "（朝一バッチで出走表が作られた後、次回のscore実行で反映されます）");
                    continue;
                }

                published++;
                var withAny = scores.Count(kv => HasAnyScore(kv.Value));
                Console.WriteLine(
                    $"[{_source.SourceName}] {slug} 6ファクター反映完了: {scores.Count}頭中{withAny}頭に" +
                    "何らかのスコアあり（血統・調教等、母集団不足やデータ未取得のものはnullのまま）");
            }

            var notes = new List<string>();
            if (skipped > 0) notes.Add($"{skipped}レースは出走表未作成のため見送り");
            if (failed > 0) notes.Add($"{failed}レースは送信失敗");
            var note = notes.Count > 0 ? $"（{string.Join("、", notes)}）" : "";
            Console.WriteLine(
                $"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 6ファクター算出 {published}レース 完了{note}");

            LogVenueTop5(targetDate, raceKeys.Values.Select(k => k.TrackCode).Distinct());

            // 送信失敗があった日は、次回のscore実行で拾い直せるよう終了コードに残す。
            if (failed > 0)
                throw new InvalidOperationException(
                    $"{failed}レースの反映に失敗しました（他のレースは反映済み）。次回のscore実行で再試行されます。");
        }

        /// <summary>仕様書§9 AI指数TOP5をログ出力する。WordPressへの実publishはContent Generator/
        /// Publisher側（Issue #5, #7）の責務のため、ここでは算出結果の可視化のみ行う。
        /// GetVenueTop5自体はpublicなScoresStore経由で他のサービスからも呼べる。</summary>
        private void LogVenueTop5(DateTime targetDate, IEnumerable<string> trackCodes)
        {
            foreach (var trackCode in trackCodes)
            {
                var top5 = _scoresStore.GetVenueTop5(targetDate, trackCode);
                if (top5.Count == 0) continue;

                Console.WriteLine($"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 場={trackCode} AI指数TOP5:");
                foreach (var r in top5)
                {
                    Console.WriteLine(
                        $"    R{r.RaceNumber} {r.Umaban}番 指数={r.AiIndex:0.0} 充足率={r.DataCompleteness:P0} " +
                        $"model={r.ModelVersion}");
                }
            }
        }

        private static bool HasAnyScore(FactorScores s) =>
            s.ParamBias.HasValue || s.ParamPace.HasValue || s.ParamAgariQ.HasValue ||
            s.ParamJockeyRoi.HasValue || s.ParamPedigreeFit.HasValue || s.ParamTrainingAcc.HasValue;

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

        private static int SafeInt(string s)
        {
            var t = Trim(s);
            return int.TryParse(t, out var v) ? v : 0;
        }

        private static string Trim(string s) => (s ?? string.Empty).Trim();
    }
}
