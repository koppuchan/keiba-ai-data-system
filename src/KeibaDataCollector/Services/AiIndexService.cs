using System;
using KeibaDataCollector.Data;
using KeibaDataCollector.Models;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 仕様書§8のAI指数仕様：FactorScoringServiceが返す6ファクター（各0〜100点、欠損はnull）を
    /// セグメント別重みで加重平均し、単一の「AI指数」に統合する。model_version・feature_version・
    /// data_cutoffの付与とscoresテーブルへの永続化もこのクラスの責務。
    ///
    /// 単純合計ではなく重み付き平均（Σ(値×重み)/Σ(重み)、null出ないファクターのみ対象）を使う。
    /// horse-race-custom-builderのフロントエンド実装で「合計方式だと算出できたファクター数が
    /// 多い馬ほど有利になる」不具合が実際に発生した教訓（同リポジトリREADME参照）を、
    /// サーバー側では最初から踏まえておく。
    /// </summary>
    public class AiIndexService
    {
        /// <summary>採点ロジック（加重平均の式、ファクター数等）を変えたら上げる。
        /// Verification DBで指数帯別成績をこの値ごとに分離するための識別子。</summary>
        public const string ModelVersion = "ai-index-v1";

        /// <summary>特徴量抽出ロジック（FactorScoringServiceの各Compute*メソッド）を変えたら上げる。
        /// モデル式は同じでも特徴量の算出方法が変われば別バージョンとして扱う。</summary>
        public const string FeatureVersion = "features-v2";

        private readonly FactorScoringService _scoring;
        private readonly ScoresStore _scores;

        public AiIndexService(FactorScoringService scoring, ScoresStore scores)
        {
            _scoring = scoring;
            _scores = scores;
        }

        /// <summary>セグメントキーを組み立てる。仕様書§8「重みはDB設定値にし、中央/地方・芝/ダート等で
        /// 別設定可能にする」に対応。トラックコードが芝/ダートのどちらか判別できない場合（地方の特殊な
        /// トラック種別等）は surface 部分を省略し、"central"/"local" 単位の設定にフォールバックする。</summary>
        public static string BuildSegment(bool isCentral, string trackSurfaceCode)
        {
            var venuePart = isCentral ? "central" : "local";
            var surface = NormalizeSurface(trackSurfaceCode);
            return surface == null ? venuePart : $"{venuePart}:{surface}";
        }

        private static string NormalizeSurface(string trackSurfaceCode)
        {
            // JV-DataのTrackCD（トラックコード）は2桁で芝/ダート/障害等を表す。
            // 大分類だけを見れば足りるため先頭1桁を代表値として使う（1x=芝, 2x=ダート）。
            if (string.IsNullOrEmpty(trackSurfaceCode)) return null;
            var head = trackSurfaceCode.Trim();
            if (head.Length == 0) return null;
            switch (head[0])
            {
                case '1': return "turf";
                case '2': return "dirt";
                default: return null; // 障害等。当面は venue 単位の重みにフォールバックする。
            }
        }

        /// <summary>1頭分を計算して即座にscoresテーブルへ保存する。</summary>
        public AiIndexResult ComputeAndPersist(FactorScoringInput input, DateTime raceDate, int raceNumber,
            int umaban, bool isCentral, DateTime dataCutoffUtc)
        {
            var factors = _scoring.Compute(input);
            var segment = BuildSegment(isCentral, input.TrackSurfaceCode);
            var weights = _scores.GetWeights(segment);

            var (index, completeness) = Combine(factors, weights);

            var result = new AiIndexResult
            {
                RaceDate = raceDate,
                TrackCode = input.TrackCode,
                RaceNumber = raceNumber,
                Umaban = umaban,
                KettoNum = input.KettoNum,
                HorseName = input.HorseName,
                JockeyCode = input.JockeyCode,
                BabaConditionCode = input.BabaConditionCode,
                Factors = factors,
                AiIndex = index,
                DataCompleteness = completeness,
                IsScratched = IsScratchedCode(input.IJyoCd),
                ModelVersion = ModelVersion,
                FeatureVersion = FeatureVersion,
                DataCutoffUtc = dataCutoffUtc,
                ComputedAtUtc = DateTime.UtcNow,
            };

            _scores.UpsertScore(result);
            return result;
        }

        /// <summary>加重平均を計算する。分母（Σ重み）が0（＝全ファクターが欠損、または
        /// 全ファクターの重みが0に設定されている）ならnullを返す。
        /// 仕様書§9「指数は結果や利益を保証するものとして表現しない」の前段として、
        /// データが無いのに0点等の数値を作らないという原則をここでも維持する。</summary>
        internal static (double? Index, double Completeness) Combine(FactorScores f, AiIndexWeights w)
        {
            double weightedSum = 0, weightTotal = 0;
            int filled = 0;

            void Add(double? value, double weight)
            {
                if (!value.HasValue || weight <= 0) return;
                weightedSum += value.Value * weight;
                weightTotal += weight;
                filled++;
            }

            Add(f.ParamBias, w.WeightBias);
            Add(f.ParamPace, w.WeightPace);
            Add(f.ParamAgariQ, w.WeightAgariQ);
            Add(f.ParamJockeyRoi, w.WeightJockeyRoi);
            Add(f.ParamPedigreeFit, w.WeightPedigreeFit);
            Add(f.ParamTrainingAcc, w.WeightTrainingAcc);

            var completeness = filled / 6.0;
            if (weightTotal <= 0) return (null, completeness);
            return (weightedSum / weightTotal, completeness);
        }

        /// <summary>JV-Data コード表2004（異常区分）: 0=異常なし、1=取消、2=除外、3=中止、
        /// 4=失格、5=降着、6=何らかの理由で著しく遅れた 等。0または空以外はすべて
        /// 「通常のレース結果として扱えない」ため、AI指数TOP5からは一律除外する
        /// （降着・失格のような着順確定後の区分がここに来るのはレース後のみで、
        /// 出走前時点では基本的に取消・除外・出走取消のいずれか）。
        /// TrendEngineService（仕様書§10）でも同じ判定が必要なためpublicにしてある。</summary>
        public static bool IsScratchedCode(string ijyoCd)
        {
            var t = (ijyoCd ?? string.Empty).Trim();
            return t.Length > 0 && t != "0";
        }
    }
}
