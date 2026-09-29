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
        /// 仕様書§14「生成後、馬名・馬番・指数・レース番号をDBと照合。不一致があれば公開停止」に対応。
        /// 馬名（表示名）を持つマスタテーブルはこのシステムにまだ無いため、より強い一意キーである
        /// 血統登録番号（ketto_num）で代用している（この制約はREADMEに明記する）。</summary>
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

            return ok;
        }
    }
}
