using System;

namespace KeibaDataCollector.Models
{
    /// <summary>
    /// 6ファクター算出のために蓄積する、1レース1頭分の履歴データ。
    ///
    /// RaceCardEntry/RaceResultEntry（WordPressへ送るその日限りの表示用データ）とは別に、
    /// こちらは複数年分をローカルSQLiteに溜め続けて統計を取るための行。
    /// 表示用モデルと統計用モデルを分けているのは、WordPress側のJSON形状（camelCase・
    /// 表示に必要な項目のみ）と、集計に必要な項目（血統コード・トラック種別・馬場状態等）が
    /// 一致しないため。
    /// </summary>
    public class HistoricalRaceEntry
    {
        public string KettoNum { get; set; }        // 血統登録番号（馬の一意キー）
        public DateTime RaceDate { get; set; }
        public string TrackCode { get; set; }        // 競馬場コード（JyoCD）
        public int RaceNumber { get; set; }           // レース番号（同日・同場・同距離の複数レースを区別するため必須）
        public string TrackSurfaceCode { get; set; }  // トラックコード（芝/ダート等）
        public int Distance { get; set; }             // 距離(m)
        public int Waku { get; set; }
        public int Umaban { get; set; }
        public string JockeyCode { get; set; }
        public string TrainerCode { get; set; }
        public int Chakujun { get; set; }             // 確定着順（0=非確定/取消等）
        public double? TanshoOdds { get; set; }
        public double? Agari3F { get; set; }          // 後3ハロンタイム(秒)
        public string CornerPassage4 { get; set; }    // 最終コーナー通過順位（生値。展開分析用）

        /// <summary>その馬の「最も早いコーナー」通過順位を、(出走頭数-1)で正規化した0〜1の値。
        /// 0=最初のコーナーを先頭で通過、1=最後尾で通過。②テン速度・展開（脚質実績）の元データ。
        /// RAレコードのコーナー通過順位（Jyuni）テキストから算出する（BackfillService参照）。</summary>
        public double? EarlyPositionRatio { get; set; }

        /// <summary>複勝払戻金額（100円あたり）。3着以内に入っていなければ0。
        /// HR（払戻）レコードから別途反映する。単勝回収率はTanshoOdds×(Chakujun==1)で
        /// 計算できるが、複勝回収率にはこの実払戻額が必要
        /// （複勝オッズは単勝オッズと別で、SEレコードには載っていない）。</summary>
        public double? FukushoPayout { get; set; }
    }

    /// <summary>坂路調教・ウッドチップ調教の1回分。両者はコース長・ハロン数が異なるため
    /// 生のラップ配列のまま保持し、正規化（最後1F相当の抽出等）は集計側で行う。</summary>
    public class TrainingLapEntry
    {
        public string KettoNum { get; set; }
        public DateTime ChokyoDate { get; set; }
        public TrainingCourse Course { get; set; }
        public string TresenKubun { get; set; }       // トレセン区分（栗東/美浦）

        /// <summary>ゴールに近い方から200mごとのラップ秒（例: [ラスト1F, その前の1F, ...]）。
        /// 坂路は4分割（800M-0M）、ウッドチップは最大10分割（2000M-0M）。
        /// 空文字列（未計測区間）はnullのまま保持する。</summary>
        public double?[] LapTimesSeconds { get; set; }
    }

    public enum TrainingCourse
    {
        Slope,      // 坂路調教（HC）
        WoodChip,   // ウッドチップ調教（WC）
    }

    /// <summary>血統：ある馬の父・母父（HansyokuNum＝繁殖登録番号）。
    /// JV-Data「19.産駒マスタ」のHansyokuNum[14]は3代血統を固定順で持つ
    /// （0:父 1:母 2:父父 3:父母 4:母父 5:母母 ...）。JV-Data仕様書Ver.4.9.0.1
    /// （フォーマットシート、項番13）で公式に確認済み。</summary>
    public class PedigreeLink
    {
        public string KettoNum { get; set; }
        public string SireHansyokuNum { get; set; }          // 父
        public string BroodmareSireHansyokuNum { get; set; } // 母父

        /// <summary>対象馬の生年月日。バックフィル時の取得範囲確認用
        /// （実機確認: option=Normalだと直近1年分しか取れなかったため、Setupに変更した。
        /// Setupで本当に1986年以降まで遡れているか、産駒の生年分布で検算する）。
        /// DBには保存せず、ログ集計にのみ使う。</summary>
        public DateTime BirthDate { get; set; }
    }

    /// <summary>繁殖馬マスタ（HN）: 繁殖登録番号→馬名。父・母父の表示名を引くために使う。</summary>
    public class BroodstockName
    {
        public string HansyokuNum { get; set; }
        public string Bamei { get; set; }
    }

    /// <summary>FactorScoringServiceへの入力。今日出走する1頭分の、レース側から分かる情報。
    /// KettoNum以外はすべてそのレースの出走表（race_card）から埋める想定。</summary>
    public class FactorScoringInput
    {
        public string KettoNum { get; set; }
        public string TrackCode { get; set; }
        public int Distance { get; set; }
        public string TrackSurfaceCode { get; set; }
        public int Waku { get; set; }
        public string JockeyCode { get; set; }

        /// <summary>SEレコードの異常区分コード（0=異常なし、1=取消、2=除外、3=中止 等。
        /// JV-Data仕様書コード表2004参照）。仕様書§9「取消・除外馬を除外」の判定に使う。
        /// 空文字/未設定は「正常」として扱う（既存の出走表取得ロジックが必ず埋めるとは
        /// 限らないため、フェイルオープンではなく実質フェイルセーフ側＝除外しない、を選択）。</summary>
        public string IJyoCd { get; set; }
    }

    /// <summary>6ファクターの算出結果（0〜100点、算出できないものはnull）。
    /// WordPress側のhrc_factorsキー名（paramBias等）にそのまま対応する。</summary>
    public class FactorScores
    {
        public double? ParamBias { get; set; }         // ①枠・馬場バイアス
        public double? ParamPace { get; set; }          // ②テン速度・展開
        public double? ParamAgariQ { get; set; }        // ③上がり3F質・末脚
        public double? ParamJockeyRoi { get; set; }      // ④騎手コース回収率
        public double? ParamPedigreeFit { get; set; }    // ⑤血統適性・妙味
        public double? ParamTrainingAcc { get; set; }    // ⑥調教・加速ラップ

        /// <summary>算出できた項目数（0〜6）。データ充足率＝ FilledCount/6。</summary>
        public int FilledCount =>
            (ParamBias.HasValue ? 1 : 0) + (ParamPace.HasValue ? 1 : 0) + (ParamAgariQ.HasValue ? 1 : 0) +
            (ParamJockeyRoi.HasValue ? 1 : 0) + (ParamPedigreeFit.HasValue ? 1 : 0) + (ParamTrainingAcc.HasValue ? 1 : 0);
    }

    /// <summary>仕様書§8「重みはDB設定値にし、中央/地方・芝/ダート等で別設定可能にする」に対応する
    /// 重み設定。1件が「セグメント」（例: central:turf, local:dirt）に対応する。</summary>
    public class AiIndexWeights
    {
        public string Segment { get; set; }
        public double WeightBias { get; set; } = 1.0;
        public double WeightPace { get; set; } = 1.0;
        public double WeightAgariQ { get; set; } = 1.0;
        public double WeightJockeyRoi { get; set; } = 1.0;
        public double WeightPedigreeFit { get; set; } = 1.0;
        public double WeightTrainingAcc { get; set; } = 1.0;
        public DateTime UpdatedAtUtc { get; set; }
    }

    /// <summary>AI指数の算出結果1頭分。scoresテーブルの1行に対応する。</summary>
    public class AiIndexResult
    {
        public DateTime RaceDate { get; set; }
        public string TrackCode { get; set; }
        public int RaceNumber { get; set; }
        public int Umaban { get; set; }
        public string KettoNum { get; set; }

        public FactorScores Factors { get; set; }

        /// <summary>Σ(値×重み)/Σ(重み)。算出できた（null出ない）ファクターのみ対象にする
        /// 重み付き平均。算出できたファクターが0件ならnull。
        /// 単純合計にすると「算出できたファクター数が多い馬」が有利になる不具合が
        /// horse-race-custom-builderのフロントエンド実装で実際に起きたため（同リポジトリREADME参照）、
        /// このサーバー側実装では最初から重み付き平均のみを採用する。</summary>
        public double? AiIndex { get; set; }

        /// <summary>0.0〜1.0。6項目中いくつ算出できたか。AI指数TOP5の同点タイブレークに使う。</summary>
        public double DataCompleteness { get; set; }

        public bool IsScratched { get; set; }

        public string ModelVersion { get; set; }
        public string FeatureVersion { get; set; }
        public DateTime DataCutoffUtc { get; set; }
        public DateTime ComputedAtUtc { get; set; }
    }
}
