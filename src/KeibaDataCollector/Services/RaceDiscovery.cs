using System;
using System.Collections.Generic;
using System.Threading;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 当日のレース一覧（キーのみ）を取得する処理。
    ///
    /// 結果監視（RaceResultService）と予想生成（PredictionService）の両方が
    /// 「その日のレースを列挙する」必要があるため、共通化している。
    /// 片方だけ直して挙動がずれる事故を避けるのが目的。
    /// </summary>
    public static class RaceDiscovery
    {
        // option=2(今週データ)はサーバー側で対象範囲を絞るため、fromtimeは範囲決定に使われない。
        // 出馬表は開催日より前に公開されるので、fromtimeを当日0時にすると公開済みの当日分を
        // 取りこぼす。十分に過去の固定値を渡して option=2 の範囲を丸ごと受け取る。
        public const string EarlyAnchorFromTime = "19860101000000";

        /// <summary>
        /// 指定日のレースキーを列挙する。
        /// "RA"レコード（レース詳細、1レース1件）を使うため "SE"（1頭1件）より効率的。
        /// </summary>
        public static List<RaceKey> ForDate(IRaceDataSource source, DateTime targetDate)
        {
            var open = source.Open("RACE", EarlyAnchorFromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode == -1)
            {
                // 該当データ無し。開催が無い日など、異常ではない。
                source.Close();
                return new List<RaceKey>();
            }
            if (open.ReturnCode < 0)
            {
                // Closeを呼ばずに抜けると次回以降のOpenが -202 で失敗し続ける。
                source.Close();
                throw new InvalidOperationException($"{source.SourceName} Open failed: {open.ReturnCode}");
            }

            var keys = new List<RaceKey>();
            try
            {
                while (true)
                {
                    int size = source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue;            // ファイル切り替わり（正常）
                    if (size == -3)
                    {
                        Thread.Sleep(500);               // ダウンロード中。busyループにしない。
                        continue;
                    }
                    if (size < 0)
                        throw new InvalidOperationException($"{source.SourceName} Read failed: {size}");

                    if (JvRecordParser.GetRecordTypeId(buffer) != "RA") continue;

                    RaceKey raceKey;
                    try
                    {
                        (raceKey, _) = JvRecordParser.ParseCornerPassage(buffer);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{source.SourceName}] RAレコードのパース失敗（このレコードのみスキップ）: {ex.Message}");
                        continue;
                    }

                    if (raceKey.RaceDate.Date == targetDate.Date)
                        keys.Add(raceKey);
                }
            }
            finally
            {
                source.Close();
            }

            return keys;
        }

        /// <summary>指定日より後で、最も近い開催日とその開催場コードを返す。無ければnull。
        /// 診断の「次回開催」用。出馬表は開催日より前に公開されるので、今週・当日データ(option=2)に
        /// 含まれる次の開催日を探す（来週分がまだ公開されていなければ見つからない）。</summary>
        public static (DateTime Date, List<string> TrackCodes)? NextRaceDay(IRaceDataSource source, DateTime after)
        {
            var open = source.Open("RACE", EarlyAnchorFromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode == -1)
            {
                source.Close();
                return null;
            }
            if (open.ReturnCode < 0)
            {
                source.Close();
                throw new InvalidOperationException($"{source.SourceName} Open failed: {open.ReturnCode}");
            }

            var tracksByDate = new SortedDictionary<DateTime, HashSet<string>>();
            var download = new DownloadWait(TimeSpan.FromSeconds(180));
            try
            {
                while (true)
                {
                    int size = source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue;
                    if (size == -3) { download.Wait(); continue; }
                    download.Reset();
                    if (size < 0)
                        throw new InvalidOperationException($"{source.SourceName} Read failed: {size}");

                    if (JvRecordParser.GetRecordTypeId(buffer) != "RA") continue;

                    RaceKey raceKey;
                    try
                    {
                        (raceKey, _) = JvRecordParser.ParseCornerPassage(buffer);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    var date = raceKey.RaceDate.Date;
                    if (date <= after.Date) continue;
                    if (!tracksByDate.TryGetValue(date, out var set))
                        tracksByDate[date] = set = new HashSet<string>();
                    set.Add(raceKey.TrackCode);
                }
            }
            finally
            {
                source.Close();
            }

            foreach (var kv in tracksByDate)
                return (kv.Key, new List<string>(kv.Value));
            return null;
        }
    }
}
