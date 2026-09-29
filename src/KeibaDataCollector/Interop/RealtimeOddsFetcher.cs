using System.Collections.Generic;
using System.Threading;
using KeibaDataCollector.Models;

namespace KeibaDataCollector.Interop
{
    /// <summary>
    /// 速報オッズ（単複枠、0B31）から単勝オッズ・単勝人気順を取得する共通処理。
    /// PredictionService（予想印生成）とContentGeneratorService（穴馬・危険な人気馬判定、
    /// 仕様書§11）の両方が同じ取得ロジックを必要とするため、ここに切り出した
    /// （元はPredictionService.FetchTanshoOddsに実装されていたもの。挙動は変更していない）。
    /// </summary>
    public static class RealtimeOddsFetcher
    {
        private const string RealtimeOddsDataSpec = "0B31";

        /// <summary>指定レースの単勝オッズ・人気順を取得する。まだ発売前、またはCOM側の
        /// エラーで取得できなかった場合はnullを返し、lastOpenReturnCodeに理由の切り分けに
        /// 使える戻り値（-1=発売前、それ以外=実エラー）を入れる。</summary>
        public static Dictionary<int, JvRecordParser.TanshoOdds> FetchTansho(
            IRaceDataSource source, RaceKey race, out int lastOpenReturnCode)
        {
            Dictionary<int, JvRecordParser.TanshoOdds> byUmaban = null;

            int rc = source.OpenRealtime(RealtimeOddsDataSpec, race.AsJvRealtimeKey());
            lastOpenReturnCode = rc;
            if (rc != 0)
            {
                // Openに対するCloseを必ず呼ぶ。呼ばずに抜けると以降のOpenが -202 で失敗し続ける。
                source.Close();
                return null;
            }

            try
            {
                while (true)
                {
                    int size = source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue;
                    if (size == -3) { Thread.Sleep(500); continue; }
                    if (size < 0) break;

                    if (JvRecordParser.GetRecordTypeId(buffer) != "O1") continue;

                    var (_, parsed) = JvRecordParser.ParseTanshoOdds(buffer);
                    if (parsed.Count > 0) byUmaban = parsed; // 後から届いたものほど新しい
                }
            }
            finally
            {
                source.Close();
            }

            return byUmaban;
        }
    }
}
