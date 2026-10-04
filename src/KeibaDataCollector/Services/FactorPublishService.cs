using System;
using System.Collections.Generic;
using System.Linq;
using KeibaDataCollector.Data;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using static KeibaDataCollector.Interop.JvDataSdk.JVData_Struct;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 当日の出走馬について6ファクター＋統合AI指数（仕様書§8・§9）を算出し、scoresテーブルへ
    /// 永続化する。ここではWordPressのhrc_factorsへは送信しない — 既存システム
    /// （horse-race-custom-builder）側の同名バッチが既にそれを行っており、この新システムが
    /// 同じ投稿へ二重に書き込むと、双方のAI指数エンジンの重み設定がSQLiteごとに独立している
    /// ため計算結果が食い違い、公開値がどちらの実行が最後だったかで揺れる事態になりうる
    /// （仕様書§2「既存サイトの予想ページ...へ連携」＝連携であって二重公開ではない、という
    /// 読み方に基づく）。ここで算出したAI指数は`scores`テーブルに保存され、Content Generator
    /// （contentコマンド）が新規の`keiba_digest`投稿へ公開する際に使われる。
    ///
    /// WordPress側の race_card（既存システムのRaceCardService経由で送信済み）には血統登録番号
    /// (KettoNum)が含まれていない（表示に不要なため元々持たせていない）。そのため、ここでは
    /// WordPress経由ではなく既存システムと同じ"RACE"データ種別を当日分だけ直接開き、SEレコード
    /// からKettoNumを取り出してHistoricalDataStoreと突き合わせる。
    ///
    /// FactorScoringServiceが返すスコアはローカルSQLiteの蓄積状況に依存する。血統(⑤)がまだ
    /// 0件（BLOD取得の問題が未解決）の間は、⑤は全馬nullのまま保存される。
    /// </summary>
    public class FactorPublishService
    {
        private readonly IRaceDataSource _source;
        private readonly AiIndexService _aiIndex;
        private readonly ScoresStore _scoresStore;

        // RaceCardServiceと同じ理由（出馬表は開催日より前に公開されるため）。
        private const string EarlyAnchorFromTime = "19860101000000";

        /// <summary>SourceNameに"中央"を含むかどうかでJV-Link/UmaConnを判別する。
        /// IRaceDataSourceインターフェース自体にIsCentral相当のプロパティを追加せず、
        /// 既存の公開プロパティ（SourceName）から導出する非侵襲的な方法を選んだ。</summary>
        private bool IsCentral => _source.SourceName.Contains("中央");

        public FactorPublishService(IRaceDataSource source, FactorScoringService scoring, ScoresStore scoresStore)
        {
            _source = source;
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

            // レースキー(slug)ごとの距離・トラック種別・馬場状態コード。RA到着時に埋め、SE処理時に参照する
            // （BackfillServiceのBackfillRaceEntriesと同じ、RA→SEの到着順を前提にした組み方）。
            var raceInfoByKey = new Dictionary<string, (int Distance, string TrackSurfaceCode, string BabaConditionCode)>();
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
                        var trackSurfaceCode = Trim(ra.TrackCD);
                        raceInfoByKey[raceKey.AsSlug()] = (SafeInt(ra.Kyori), trackSurfaceCode, BabaConditionFor(trackSurfaceCode, ra));
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
                            HorseName = Trim(se.Bamei),
                            TrackCode = raceKey.TrackCode,
                            Distance = raceInfo.Distance,
                            TrackSurfaceCode = raceInfo.TrackSurfaceCode,
                            BabaConditionCode = raceInfo.BabaConditionCode,
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

            int computed = 0, failed = 0;
            var coverage = new FactorCoverage();
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

                computed++;
                foreach (var f in scores.Values) coverage.Add(f);
                var withAny = scores.Count(kv => HasAnyScore(kv.Value));
                Console.WriteLine(
                    $"[{_source.SourceName}] {slug} 6ファクター/AI指数算出完了: {scores.Count}頭中{withAny}頭に" +
                    "何らかのスコアあり（血統・調教等、母集団不足やデータ未取得のものはnullのまま）");
            }

            var note = failed > 0 ? $"（{failed}レースは計算失敗）" : "";
            Console.WriteLine(
                $"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 6ファクター/AI指数算出 {computed}レース 完了{note}");

            Console.WriteLine($"[{_source.SourceName}] 算出できた馬の割合（全{coverage.Total}頭）: {coverage}");

            LogVenueTop5(targetDate, raceKeys.Values.Select(k => k.TrackCode).Distinct());

            // 計算失敗があった日は、次回のscore実行で拾い直せるよう終了コードに残す。
            if (failed > 0)
                throw new InvalidOperationException(
                    $"{failed}レースの計算に失敗しました（他のレースは算出済み）。次回のscore実行で再試行されます。");
        }

        /// <summary>仕様書§9 AI指数TOP5をログ出力する。WordPressへの実publishはContent Generator/
        /// Publisher側の責務のため、ここでは算出結果の可視化のみ行う。
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

        /// <summary>ファクター別に「算出できた馬の数」を数える。データ充足率が低い原因が
        /// どのファクター（血統・調教等）のデータ欠損かを、ログだけで切り分けられるようにする。</summary>
        private sealed class FactorCoverage
        {
            private int _bias, _pace, _agari, _jockey, _pedigree, _training;
            public int Total { get; private set; }

            public void Add(FactorScores f)
            {
                Total++;
                if (f.ParamBias.HasValue) _bias++;
                if (f.ParamPace.HasValue) _pace++;
                if (f.ParamAgariQ.HasValue) _agari++;
                if (f.ParamJockeyRoi.HasValue) _jockey++;
                if (f.ParamPedigreeFit.HasValue) _pedigree++;
                if (f.ParamTrainingAcc.HasValue) _training++;
            }

            public override string ToString()
            {
                string P(int n) => Total == 0 ? "-" : $"{(double)n / Total:P0}";
                return $"①枠馬場={P(_bias)} ②テン速度={P(_pace)} ③上がり={P(_agari)} " +
                       $"④騎手={P(_jockey)} ⑤血統={P(_pedigree)} ⑥調教={P(_training)}";
            }
        }

        private static bool HasAnyScore(FactorScores s) =>
            s.ParamBias.HasValue || s.ParamPace.HasValue || s.ParamAgariQ.HasValue ||
            s.ParamJockeyRoi.HasValue || s.ParamPedigreeFit.HasValue || s.ParamTrainingAcc.HasValue;

        /// <summary>そのレースの馬場種別（芝/ダート）に対応する馬場状態コードを選ぶ。
        /// TrackCD（トラックコード）の先頭桁は1x=芝、2x=ダートを表す（AiIndexService.BuildSegmentの
        /// NormalizeSurfaceと同じ判定）。障害等どちらにも当てはまらない場合はnull。
        /// 仕様書§21「馬場変更が反映される」の検知（Validator）に使う。</summary>
        private static string BabaConditionFor(string trackSurfaceCode, JV_RA_RACE ra)
        {
            if (string.IsNullOrEmpty(trackSurfaceCode)) return null;
            switch (trackSurfaceCode[0])
            {
                case '1': return Trim(ra.TenkoBaba.SibaBabaCD);
                case '2': return Trim(ra.TenkoBaba.DirtBabaCD);
                default: return null;
            }
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

        private static int SafeInt(string s)
        {
            var t = Trim(s);
            return int.TryParse(t, out var v) ? v : 0;
        }

        private static string Trim(string s) => (s ?? string.Empty).Trim();
    }
}
