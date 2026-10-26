using System;
using System.Collections.Generic;
using System.Threading;

namespace KeibaDataCollector.Interop
{
    /// <summary>
    /// 1回の実行の中で、同じ「RACE 今週・当日」の読み込みを1度だけ行い、2回目以降はメモリから返す。
    ///
    /// これまでは、開催場の列挙に1回、さらに開催場ごとに1回ずつ、同じRACEデータを全件読み直していた
    /// （contentで6会場なら7回）。1回の読み込みに数分かかるため、実行が20分を超えて、次回の起動や
    /// 時間制限とぶつかっていた（実測 2026-10-13）。
    /// RACE（ThisWeekAndToday、基準時刻が EarlyAnchor）以外の呼び出し（速報の OpenRealtime、
    /// 蓄積取得のNormal等）は、そのまま元のソースに渡す。
    /// </summary>
    public sealed class CachingRaceSource : IRaceDataSource
    {
        // 保持する上限（文字数）。これを超えるのは想定外のため、メモリ不足で落ちる前に明確なエラーにする。
        private const long MaxCachedChars = 20000000;

        private readonly IRaceDataSource _inner;
        private readonly string _targetDate;

        private OpenResult _cachedOpen;
        private List<string> _cachedRecords;

        private bool _replaying;
        private int _replayIndex;

        /// <param name="targetDate">対象日。この日のレコード（レースID中の開催年月日が一致するもの）だけ保持する。
        /// 利用側（開催場列挙・AI指数・コンテンツ・傾向・検証）はどれも対象日以外を読み捨てているため、
        /// 結果は変わらない。全期間を保持すると、32bitプロセスでUmaConnの全件がメモリに載りきらず、
        /// OutOfMemoryで地方競馬の更新が10/14から止まった。</param>
        public CachingRaceSource(IRaceDataSource inner, DateTime targetDate)
        {
            _inner = inner;
            _targetDate = targetDate.ToString("yyyyMMdd");
        }

        public string SourceName => _inner.SourceName;

        public void Initialize(string softwareId) => _inner.Initialize(softwareId);

        public void RunInteractiveSetup() => _inner.RunInteractiveSetup();

        private static bool IsCacheable(string dataSpec, string fromTime, DataOption option) =>
            dataSpec == "RACE" && option == DataOption.ThisWeekAndToday && fromTime == "19860101000000";

        public OpenResult Open(string dataSpec, string fromTime, DataOption option)
        {
            if (!IsCacheable(dataSpec, fromTime, option))
            {
                _replaying = false;
                return _inner.Open(dataSpec, fromTime, option);
            }

            if (_cachedOpen == null) LoadFromSource(dataSpec, fromTime, option);

            _replaying = true;
            _replayIndex = 0;
            return new OpenResult
            {
                ReturnCode = _cachedOpen.ReturnCode,
                ReadCount = _cachedOpen.ReadCount,
                DownloadCount = _cachedOpen.DownloadCount,
                LastFileTimestamp = _cachedOpen.LastFileTimestamp,
            };
        }

        /// <summary>元のソースから全件読んでメモリに保持する。失敗（例外）した場合は何も保持しない
        /// （次の呼び出しで読み直す）。</summary>
        private void LoadFromSource(string dataSpec, string fromTime, DataOption option)
        {
            var open = _inner.Open(dataSpec, fromTime, option);
            var records = new List<string>();
            long cachedChars = 0;
            if (open.ReturnCode < 0)
            {
                // -1（該当データ無し）等はそのまま呼び出し側に返す。呼び出し側がCloseする。
                _inner.Close();
                _cachedOpen = open;
                _cachedRecords = records;
                return;
            }

            var download = new DownloadWait(TimeSpan.FromSeconds(180));
            try
            {
                while (true)
                {
                    int size = _inner.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue;                         // ファイル切り替わり（正常）
                    if (size == -3) { download.Wait(); continue; }    // ダウンロード中
                    download.Reset();
                    if (size < 0)
                        throw new InvalidOperationException($"{_inner.SourceName} Read failed: {size}");

                    // レースID（レコード種別2＋データ区分1＋作成日8の次の、開催年4＋月日4）が対象日のものだけ残す。
                    if (buffer.Length < 19 || string.CompareOrdinal(buffer, 11, _targetDate, 0, 8) != 0) continue;

                    cachedChars += buffer.Length;
                    if (cachedChars > MaxCachedChars)
                        throw new InvalidOperationException(
                            $"{_inner.SourceName} 対象日のRACEデータが想定より大きいため、読み込みを中止しました。");
                    records.Add(buffer);
                }
            }
            finally
            {
                _inner.Close();
            }

            _cachedOpen = open;
            _cachedRecords = records;
        }

        public int Read(out string buffer, out string fileName)
        {
            if (!_replaying) return _inner.Read(out buffer, out fileName);

            fileName = string.Empty;
            if (_cachedRecords == null || _replayIndex >= _cachedRecords.Count)
            {
                buffer = string.Empty;
                return 0;
            }
            buffer = _cachedRecords[_replayIndex++];
            return buffer.Length;
        }

        public void Close()
        {
            if (_replaying)
            {
                _replaying = false;
                return;
            }
            _inner.Close();
        }

        public int OpenRealtime(string dataSpec, string key)
        {
            _replaying = false;
            return _inner.OpenRealtime(dataSpec, key);
        }

        /// <summary>元のソースは呼び出し側（Program）が所有するため、ここでは解放しない。</summary>
        public void Dispose() { }
    }
}
