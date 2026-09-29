using System;
using System.Linq;
using KeibaDataCollector.Data;
using KeibaDataCollector.Models;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 仕様書§14 AI文章生成・検証、および§5 LicenseGateの公開経路への実接続。
    ///
    /// ContentGeneratorService（Issue #5）が作った1件ずつのGeneratedPickを、公開する直前に
    /// この場所で最終チェックする。
    ///   1. DB照合: 生成時点で見ていた馬番・血統登録番号・AI指数が、今のscoresテーブルの値と
    ///      矛盾していないか（不一致があれば「生成後にレース情報が変わった」ことを意味する）。
    ///   2. LicenseGate: この開催場が今Web公開してよい状態か（仕様書§5「取得できる」≠
    ///      「公開してよい」の実運用ゲート。ここが最終防衛線）。
    /// どちらか一方でも不合格ならValidatorPassed=falseとし、呼び出し側（将来のPublisher、
    /// Issue #7）はそれを見て公開を止める。
    ///
    /// 判定結果は合否にかかわらずpredictionsテーブルへimmutableに保存する（不合格分も含めて
    /// 保存するのは、「なぜ公開されなかったか」を後から追跡できるようにするため。
    /// 仕様書§17監視ダッシュボードの「公開停止理由」はここが情報源になる）。
    /// </summary>
    public class ValidatorService
    {
        private readonly ScoresStore _scores;
        private readonly LicenseGateStore _licenseGate;
        private readonly PredictionStore _predictions;

        /// <summary>AI指数の再照合の許容誤差（絶対値）。生成〜検証の間に対象馬のデータが
        /// 何も変わっていなければ完全一致するはずだが、historical.sqlite3側の並行backfill等で
        /// 母集団が微小に変動する可能性を考慮し、わずかな揺れは許容する。これを超える差は
        /// 「生成後に何かが実質的に変わった」とみなして不合格にする。</summary>
        private const double AiIndexToleranceAbs = 2.0;

        public ValidatorService(ScoresStore scores, LicenseGateStore licenseGate, PredictionStore predictions)
        {
            _scores = scores;
            _licenseGate = licenseGate;
            _predictions = predictions;
        }

        public ValidationOutcome ValidateAndSnapshot(GeneratedPick pick, bool isCentral)
        {
            var notes = new System.Collections.Generic.List<string>();
            var dataOk = CheckAgainstCurrentData(pick, notes);
            var licenseOk = _licenseGate.IsWebPublishAllowed(pick.Race.TrackCode, isCentral);
            if (!licenseOk)
                notes.Add("LicenseGate: この開催場は現在Web公開が許可されていません。");

            var passed = dataOk && licenseOk;

            var record = new PredictionRecord
            {
                PredictionId = Guid.NewGuid().ToString("N"),
                RaceDate = pick.Race.RaceDate,
                TrackCode = pick.Race.TrackCode,
                RaceNumber = pick.Race.RaceNumber,
                Umaban = pick.Umaban,
                KettoNum = pick.KettoNum,
                HorseName = pick.HorseNameAtGeneration,
                JockeyCode = pick.JockeyCodeAtGeneration,
                Category = pick.Category.ToString(),
                ContentText = pick.Text,
                Reasons = pick.Reasons,
                AiIndexSnapshot = pick.AiIndexAtGeneration,
                NinkiSnapshot = pick.NinkiAtGeneration,
                TanshoOddsSnapshot = pick.TanshoOddsAtGeneration,
                ModelVersion = AiIndexService.ModelVersion,
                LicenseCheckPassed = licenseOk,
                ValidatorPassed = passed,
                ValidatorNotes = notes.Count > 0 ? string.Join(" / ", notes) : null,
                CreatedAtUtc = DateTime.UtcNow,
            };
            _predictions.Insert(record);

            return new ValidationOutcome { PredictionId = record.PredictionId, Passed = passed, Notes = notes };
        }

        /// <summary>生成時点でGeneratedPickが参照していた値と、今のscoresテーブルの値を突き合わせる。
        /// 仕様書§14「生成後、馬名・馬番・指数・レース番号をDBと照合。不一致があれば公開停止」に対応
        /// （馬名照合はIssue #13で追加。SEレコードのBameiをscores.horse_nameに保存している）。</summary>
        private bool CheckAgainstCurrentData(GeneratedPick pick, System.Collections.Generic.List<string> notes)
        {
            var current = _scores.GetRaceScores(pick.Race.RaceDate, pick.Race.TrackCode, pick.Race.RaceNumber)
                .FirstOrDefault(s => s.Umaban == pick.Umaban);

            if (current == null)
            {
                notes.Add($"{pick.Umaban}番のスコアが現在のscoresテーブルに存在しません（取消・除外、またはscore未実行の可能性）。");
                return false;
            }

            var ok = true;

            if (current.IsScratched)
            {
                ok = false;
                notes.Add($"{pick.Umaban}番は現在、取消・除外扱いです。");
            }

            if (!string.IsNullOrEmpty(pick.KettoNum) && !string.IsNullOrEmpty(current.KettoNum)
                && pick.KettoNum != current.KettoNum)
            {
                ok = false;
                notes.Add($"血統登録番号が生成時と一致しません（生成時={pick.KettoNum}, 現在={current.KettoNum}）。" +
                          "出走取消・馬番変更等で対象馬が入れ替わった可能性があります。");
            }

            if (!string.IsNullOrEmpty(pick.HorseNameAtGeneration) && !string.IsNullOrEmpty(current.HorseName)
                && pick.HorseNameAtGeneration != current.HorseName)
            {
                ok = false;
                notes.Add($"馬名が生成時（{pick.HorseNameAtGeneration}）と現在（{current.HorseName}）で一致しません。" +
                          "対象馬が入れ替わった可能性があります。");
            }

            if (pick.AiIndexAtGeneration.HasValue)
            {
                if (!current.AiIndex.HasValue)
                {
                    ok = false;
                    notes.Add($"AI指数が生成時（{pick.AiIndexAtGeneration:0.00}）から現在は算出不能に変わっています。");
                }
                else if (Math.Abs(current.AiIndex.Value - pick.AiIndexAtGeneration.Value) > AiIndexToleranceAbs)
                {
                    ok = false;
                    notes.Add($"AI指数が生成時（{pick.AiIndexAtGeneration:0.00}）から現在（{current.AiIndex:0.00}）へ許容誤差を超えて変化しています。");
                }
            }

            // 仕様書§15「騎手変更→再計算」。ここでは検知のみ行い公開停止にする。
            // 「再計算」自体は、score/contentコマンドが冪等かつ1日に何度も再実行される前提の
            // 設計になっているため、次回の定期実行で新しい騎手コードを反映した予測が
            // 自然に生成される（自動で即時再計算をその場でトリガーする作りにはしていない。
            // JV-Link/UmaConnの再取得は数十秒〜のオーダーで、Validator実行のたびに
            // 割り込ませるとcontentコマンド全体の実行時間が不安定になるため）。
            if (!string.IsNullOrEmpty(pick.JockeyCodeAtGeneration) && !string.IsNullOrEmpty(current.JockeyCode)
                && pick.JockeyCodeAtGeneration != current.JockeyCode)
            {
                ok = false;
                notes.Add($"騎手が生成時（{pick.JockeyCodeAtGeneration}）から現在（{current.JockeyCode}）へ変更されています。" +
                          "次回のscore/content実行で新しい騎手を反映して再計算されます。");
            }

            return ok;
        }
    }
}
