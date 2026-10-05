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
    /// 仕様書§18 レース後の自動検証。predictions（狙い馬/穴馬/危険な人気馬・AI指数TOP5、
    /// immutableに保存済み）を確定着順と突き合わせ、的中率を集計可能な形で
    /// verificationテーブルへ保存する。
    ///
    /// 確定着順は当日のSEレコードを直接読んで得る（TrendEngineService/ContentGeneratorServiceと
    /// 同じ理由: historical.sqlite3への当日結果の反映はリアルタイムではないため）。
    ///
    /// 「不的中データも削除せず全件検証に含める」（仕様書§18）方針のため、Validatorに合格した
    /// predictionsは的中・不的中を問わずすべて検証対象にする。Validator不合格（公開されなかった）
    /// ものは検証対象にしない（公開していない予測の的中率を集計しても意味が無いため）。
    /// </summary>
    public class VerificationService
    {
        private readonly IRaceDataSource _source;
        private readonly PredictionStore _predictions;
        private readonly VerificationStore _verification;

        private const string EarlyAnchorFromTime = "19860101000000";

        public VerificationService(IRaceDataSource source, PredictionStore predictions, VerificationStore verification)
        {
            _source = source;
            _predictions = predictions;
            _verification = verification;
        }

        public VerificationRunSummary VerifyVenue(DateTime raceDate, string trackCode)
        {
            var (results, surfaceByRace) = ReadTodayResults(raceDate, trackCode);
            var alreadyVerified = _verification.GetVerifiedPredictionIds(raceDate, trackCode);
            var predictions = _predictions.GetForVenue(raceDate, trackCode)
                .Where(p => p.ValidatorPassed) // 非公開だったものは検証対象にしない。
                .ToList();

            int verified = 0, pending = 0;
            foreach (var p in predictions)
            {
                if (alreadyVerified.Contains(p.PredictionId)) continue;

                if (!results.TryGetValue((p.RaceNumber, p.Umaban), out var chakujun))
                {
                    pending++; // まだレースが確定していない。次回のverify実行で拾う。
                    continue;
                }

                _verification.Upsert(new VerificationRecord
                {
                    PredictionId = p.PredictionId,
                    RaceDate = p.RaceDate,
                    TrackCode = p.TrackCode,
                    RaceNumber = p.RaceNumber,
                    Umaban = p.Umaban,
                    Category = p.Category,
                    ModelVersion = p.ModelVersion,
                    Chakujun = chakujun,
                    HitTop3 = chakujun >= 1 && chakujun <= 3,
                    HitWin = chakujun == 1,
                    VerifiedAtUtc = DateTime.UtcNow,
                    TrackSurfaceCode = surfaceByRace.TryGetValue(p.RaceNumber, out var surface) ? surface : null,
                });
                verified++;
            }

            return new VerificationRunSummary
            {
                TotalPredictions = predictions.Count,
                Verified = verified,
                StillPending = pending,
            };
        }

        /// <summary>(race_number, umaban) -> 確定着順（chakujun>0＝確定済みの行のみ）と、
        /// レース番号 -> トラックコード（サイトの芝・ダート別集計用）。</summary>
        private (Dictionary<(int RaceNumber, int Umaban), int> Results, Dictionary<int, string> SurfaceByRace)
            ReadTodayResults(DateTime targetDate, string trackCode)
        {
            var result = new Dictionary<(int, int), int>();
            var surfaceByRace = new Dictionary<int, string>();

            var open = _source.Open("RACE", EarlyAnchorFromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode == -1) { _source.Close(); return (result, surfaceByRace); }
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

                    var typeId = JvRecordParser.GetRecordTypeId(buffer);
                    if (typeId == "RA")
                    {
                        var ra = new JV_RA_RACE();
                        ra.SetDataB(ref buffer);
                        var (raKey, raOk) = TryBuildRaceKey(ra.id.Year, ra.id.MonthDay, ra.id.JyoCD, ra.id.RaceNum, targetDate);
                        if (raOk && raKey.TrackCode == trackCode)
                            surfaceByRace[raKey.RaceNumber] = Trim(ra.TrackCD);
                        continue;
                    }
                    if (typeId != "SE") continue;

                    var se = new JV_SE_RACE_UMA();
                    se.SetDataB(ref buffer);
                    var (raceKey, ok) = TryBuildRaceKey(se.id.Year, se.id.MonthDay, se.id.JyoCD, se.id.RaceNum, targetDate);
                    if (!ok || raceKey.TrackCode != trackCode) continue;

                    var umaban = SafeInt(se.Umaban);
                    var chakujun = SafeInt(se.KakuteiJyuni);
                    if (umaban <= 0 || chakujun <= 0) continue; // 未確定・取消等は対象外。

                    result[(raceKey.RaceNumber, umaban)] = chakujun;
                }
            }
            finally
            {
                _source.Close();
            }

            return (result, surfaceByRace);
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
