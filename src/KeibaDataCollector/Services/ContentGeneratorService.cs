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
    /// 仕様書§11「今日の狙い馬・穴馬・危険な人気馬」。
    ///
    /// AIに数値や馬番を自由生成させない（仕様書§14）方針のため、この段階では自然文生成AIを
    /// 呼ばない。DBの実数値だけを埋め込んだ固定テンプレートで文章を組み立てる
    /// （テンプレートに断定表現を入れないことをGuardAgainstAbsoluteWordingで機械的にも担保する）。
    ///
    /// 判定に使う指数はScoresStore（scoreコマンドで既に永続化済み）から読むが、取消・除外状態は
    /// scoreコマンド実行時点のスナップショットの可能性があるため、このサービス自身が実行時に
    /// 当日のSEレコードを読み直して最新の状態で上書きする（仕様書§11「取消・騎手変更・馬場変更が
    /// 未反映なら公開停止または再計算」のうち、取消・除外の再計算を担当）。騎手変更・馬名不一致は
    /// scoresテーブルに算出時の値（jockey_code/horse_name）が保存されているため、公開直前に
    /// Validatorが現在値と再照合する形でカバーする。</summary>
    public class ContentGeneratorService
    {
        private readonly IRaceDataSource _source;
        private readonly ScoresStore _scores;

        private const string EarlyAnchorFromTime = "19860101000000";

        // 狙い馬: 当該レース内でAI指数トップ、かつ最低限の充足率・指数水準を満たすこと。
        private const double NeraiMinDataCompleteness = 0.5;   // 6項目中3項目以上
        private const double NeraiMinAiIndex = 55.0;           // 偏差値50が平均。半偏差値以上を「基準以上」とする

        // 穴馬: 人気が薄い（6番人気以下）にもかかわらず、レース内のAI指数順位が上位半分に入ること。
        private const int AnaMinNinki = 6;
        private const double AnaMinDataCompleteness = 0.34;    // 6項目中2項目以上

        // 危険な人気馬: 人気が高い（3番人気以内）のに、指数またはいずれかの項目に注意点があること。
        private const int KikenMaxNinki = 3;
        private const double KikenFactorConcernThreshold = 40.0; // 偏差値40未満＝平均より1標準偏差以上低い

        private static readonly string[] BannedWords = { "絶対", "確実", "間違いない", "必ず", "100%" };

        public ContentGeneratorService(IRaceDataSource source, ScoresStore scores)
        {
            _source = source;
            _scores = scores;
        }

        public List<GeneratedPick> GenerateForVenue(DateTime raceDate, string trackCode)
        {
            var currentEntries = ReadCurrentEntries(raceDate, trackCode);
            var picks = new List<GeneratedPick>();

            foreach (var raceNumber in currentEntries.Keys.OrderBy(n => n))
            {
                var raceKey = new RaceKey { RaceDate = raceDate, TrackCode = trackCode, RaceNumber = raceNumber };
                var candidates = BuildCandidates(raceDate, trackCode, raceNumber, currentEntries[raceNumber]);
                if (candidates.Count == 0) continue;

                var nerai = PickNerai(raceKey, candidates);
                if (nerai != null) picks.Add(nerai);

                var ana = PickAna(raceKey, candidates);
                if (ana != null) picks.Add(ana);

                picks.AddRange(PickKiken(raceKey, candidates));
            }

            foreach (var pick in picks)
                GuardAgainstAbsoluteWording(pick.Text);

            return picks;
        }

        private List<PickCandidate> BuildCandidates(
            DateTime raceDate, string trackCode, int raceNumber, Dictionary<int, bool> currentScratchByUmaban)
        {
            var scores = _scores.GetRaceScores(raceDate, trackCode, raceNumber);
            var raceKey = new RaceKey { RaceDate = raceDate, TrackCode = trackCode, RaceNumber = raceNumber };
            var odds = RealtimeOddsFetcher.FetchTansho(_source, raceKey, out _);

            var candidates = new List<PickCandidate>();
            foreach (var s in scores)
            {
                // scoresテーブルに無い（scoreコマンドがまだ実行されていない）馬は判定不能なので除く。
                var isCurrentlyScratched = currentScratchByUmaban.TryGetValue(s.Umaban, out var scratched)
                    ? scratched
                    : s.IsScratched; // 当日データに存在しない＝最新の出走表取得より前の情報。安全側でscore時点の値を使う。

                if (isCurrentlyScratched) continue; // 取消・除外は候補に含めない。

                var candidate = new PickCandidate
                {
                    RaceNumber = raceNumber,
                    Umaban = s.Umaban,
                    KettoNum = s.KettoNum,
                    HorseName = s.HorseName,
                    JockeyCode = s.JockeyCode,
                    BabaConditionCode = s.BabaConditionCode,
                    AiIndex = s.AiIndex,
                    DataCompleteness = s.DataCompleteness,
                    Factors = s.Factors,
                    IsCurrentlyScratched = false,
                };

                if (odds != null && odds.TryGetValue(s.Umaban, out var o))
                {
                    candidate.Ninki = o.Ninki;
                    candidate.TanshoOdds = o.Odds;
                }

                candidates.Add(candidate);
            }
            return candidates;
        }

        // ---- 狙い馬 ----

        private GeneratedPick PickNerai(RaceKey race, List<PickCandidate> candidates)
        {
            var best = candidates
                .Where(c => c.AiIndex.HasValue && c.DataCompleteness >= NeraiMinDataCompleteness && c.AiIndex.Value >= NeraiMinAiIndex)
                .OrderByDescending(c => c.AiIndex.Value)
                .FirstOrDefault();
            if (best == null) return null;

            var topFactor = TopFactor(best.Factors);
            var horse = DisplayHorse(best);
            var text = topFactor == null
                ? $"{race.RaceNumber}Rの狙い馬は{horse}。AI指数{best.AiIndex:0.0}（データ充足率{best.DataCompleteness:P0}）で当レース内トップ評価。"
                : $"{race.RaceNumber}Rの狙い馬は{horse}。AI指数{best.AiIndex:0.0}（データ充足率{best.DataCompleteness:P0}）で当レース内トップ評価。{topFactor.Value.Name}が特に高評価（{topFactor.Value.Value:0.0}）。";

            return new GeneratedPick
            {
                Category = PickCategory.Nerai,
                Race = race,
                Umaban = best.Umaban,
                KettoNum = best.KettoNum,
                HorseName = best.HorseName,
                Text = text,
                Reasons = { $"ai_index={best.AiIndex:0.00}", $"data_completeness={best.DataCompleteness:0.00}" },
                AiIndexAtGeneration = best.AiIndex,
                NinkiAtGeneration = best.Ninki,
                TanshoOddsAtGeneration = best.TanshoOdds,
                JockeyCodeAtGeneration = best.JockeyCode,
                HorseNameAtGeneration = best.HorseName,
                BabaConditionCodeAtGeneration = best.BabaConditionCode,
            };
        }

        // ---- 穴馬 ----

        private GeneratedPick PickAna(RaceKey race, List<PickCandidate> candidates)
        {
            var withOdds = candidates.Where(c => c.Ninki.HasValue && c.AiIndex.HasValue).ToList();
            if (withOdds.Count == 0) return null; // オッズ未取得時は判定しない（仕様書§11）。

            var fieldSize = withOdds.Count;
            var rankedByIndex = withOdds.OrderByDescending(c => c.AiIndex.Value).ToList();

            var best = withOdds
                .Where(c => c.Ninki.Value >= AnaMinNinki && c.DataCompleteness >= AnaMinDataCompleteness)
                .Where(c => rankedByIndex.IndexOf(c) < Math.Ceiling(fieldSize / 2.0)) // レース内指数順位が上位半分
                .OrderByDescending(c => c.AiIndex.Value)
                .FirstOrDefault();
            if (best == null) return null;

            var indexRank = rankedByIndex.IndexOf(best) + 1;
            var text = $"{race.RaceNumber}Rの穴馬は{DisplayHorse(best)}。{best.Ninki}番人気（単勝{best.TanshoOdds:0.0}倍）ながら、" +
                       $"AI指数は{best.AiIndex:0.0}で出走{fieldSize}頭中{indexRank}位相当。人気とのギャップに妙味あり。";

            return new GeneratedPick
            {
                Category = PickCategory.Ana,
                Race = race,
                Umaban = best.Umaban,
                KettoNum = best.KettoNum,
                HorseName = best.HorseName,
                Text = text,
                Reasons = { $"ninki={best.Ninki}", $"ai_index={best.AiIndex:0.00}", $"index_rank={indexRank}/{fieldSize}" },
                AiIndexAtGeneration = best.AiIndex,
                NinkiAtGeneration = best.Ninki,
                TanshoOddsAtGeneration = best.TanshoOdds,
                JockeyCodeAtGeneration = best.JockeyCode,
                HorseNameAtGeneration = best.HorseName,
                BabaConditionCodeAtGeneration = best.BabaConditionCode,
            };
        }

        // ---- 危険な人気馬 ----

        private List<GeneratedPick> PickKiken(RaceKey race, List<PickCandidate> candidates)
        {
            var result = new List<GeneratedPick>();
            var withOdds = candidates.Where(c => c.Ninki.HasValue).ToList();
            if (withOdds.Count == 0) return result; // オッズ未取得時は判定しない。

            var favorites = withOdds.Where(c => c.Ninki.Value <= KikenMaxNinki);
            foreach (var c in favorites)
            {
                var concern = WeakestFactor(c.Factors, KikenFactorConcernThreshold);
                if (concern == null) continue; // 明確な弱点が無ければ「危険」とは書かない。

                var text = $"{race.RaceNumber}Rの{c.Ninki}番人気{DisplayHorse(c)}は、{concern.Value.Name}が{concern.Value.Value:0.0}と平均を下回っており注意。" +
                           (c.AiIndex.HasValue ? $"AI指数は{c.AiIndex:0.0}。" : "AI指数は算出できていない。");

                result.Add(new GeneratedPick
                {
                    Category = PickCategory.Kiken,
                    Race = race,
                    Umaban = c.Umaban,
                    KettoNum = c.KettoNum,
                    HorseName = c.HorseName,
                    Text = text,
                    Reasons = { $"ninki={c.Ninki}", $"weak_factor={concern.Value.Name}={concern.Value.Value:0.00}" },
                    AiIndexAtGeneration = c.AiIndex,
                    NinkiAtGeneration = c.Ninki,
                    TanshoOddsAtGeneration = c.TanshoOdds,
                    JockeyCodeAtGeneration = c.JockeyCode,
                    HorseNameAtGeneration = c.HorseName,
                    BabaConditionCodeAtGeneration = c.BabaConditionCode,
                });
            }
            return result;
        }

        // ---- 当日の最新出走状態（取消・除外）の読み直し ----

        /// <summary>race_number -> (umaban -> 現在の取消・除外状態)。</summary>
        private Dictionary<int, Dictionary<int, bool>> ReadCurrentEntries(DateTime targetDate, string trackCode)
        {
            var result = new Dictionary<int, Dictionary<int, bool>>();

            var open = _source.Open("RACE", EarlyAnchorFromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode == -1) { _source.Close(); return result; }
            if (open.ReturnCode < 0)
            {
                _source.Close();
                throw new InvalidOperationException($"{_source.SourceName} RACE Open failed: {open.ReturnCode}");
            }

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

                    if (JvRecordParser.GetRecordTypeId(buffer) != "SE") continue;

                    var se = new JV_SE_RACE_UMA();
                    se.SetDataB(ref buffer);
                    var (raceKey, ok) = TryBuildRaceKey(se.id.Year, se.id.MonthDay, se.id.JyoCD, se.id.RaceNum, targetDate);
                    if (!ok || raceKey.TrackCode != trackCode) continue;

                    var umaban = SafeInt(se.Umaban);
                    if (umaban <= 0) continue;

                    if (!result.TryGetValue(raceKey.RaceNumber, out var byUmaban))
                    {
                        byUmaban = new Dictionary<int, bool>();
                        result[raceKey.RaceNumber] = byUmaban;
                    }
                    byUmaban[umaban] = AiIndexService.IsScratchedCode(Trim(se.IJyoCD));
                }
            }
            finally
            {
                _source.Close();
            }

            return result;
        }

        // ---- テンプレート補助 ----

        /// <summary>「3番タニノフランケル」のように馬番＋馬名で表示する。馬名が未取得
        /// （scoreコマンド未実行の古いデータ等）の場合は馬番のみにフォールバックする。</summary>
        private static string DisplayHorse(PickCandidate c) =>
            string.IsNullOrEmpty(c.HorseName) ? $"{c.Umaban}番" : $"{c.Umaban}番{c.HorseName}";

        private static (string Name, double Value)? TopFactor(FactorScores f)
        {
            var named = NamedFactors(f).Where(x => x.Value.HasValue).OrderByDescending(x => x.Value.Value).FirstOrDefault();
            return named.Value.HasValue ? (named.Name, named.Value.Value) : ((string, double)?)null;
        }

        private static (string Name, double Value)? WeakestFactor(FactorScores f, double threshold)
        {
            var named = NamedFactors(f).Where(x => x.Value.HasValue && x.Value.Value < threshold)
                .OrderBy(x => x.Value.Value).FirstOrDefault();
            return named.Value.HasValue ? (named.Name, named.Value.Value) : ((string, double)?)null;
        }

        private static IEnumerable<(string Name, double? Value)> NamedFactors(FactorScores f)
        {
            yield return ("枠・馬場バイアス", f.ParamBias);
            yield return ("テン速度・展開", f.ParamPace);
            yield return ("上がり3F・末脚", f.ParamAgariQ);
            yield return ("騎手コース回収率", f.ParamJockeyRoi);
            yield return ("血統適性・妙味", f.ParamPedigreeFit);
            yield return ("調教・加速ラップ", f.ParamTrainingAcc);
        }

        /// <summary>仕様書§11「絶対」「確実」等の断定表現を禁止、の機械的な最終防衛線。
        /// テンプレート自体に含めていないため通常は発火しないが、将来のテンプレート追加・変更で
        /// 混入した場合に公開前に気づけるようにする。</summary>
        private static void GuardAgainstAbsoluteWording(string text)
        {
            foreach (var word in BannedWords)
            {
                if (text.Contains(word))
                    throw new InvalidOperationException($"生成文に禁止表現「{word}」が含まれています: {text}");
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
