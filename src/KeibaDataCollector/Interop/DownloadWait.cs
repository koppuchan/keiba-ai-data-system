using System;
using System.Threading;

namespace KeibaDataCollector.Interop
{
    /// <summary>
    /// JVRead/JVGetsが -3（ダウンロード中）を返し続けるときの待ち。以前は上限が無く、提供元の応答が
    /// 止まると永遠に待ち、周期全体が30分の強制終了まで固まった（実測 2026-10-26）。
    /// 連続して待った時間が上限を超えたらTimeoutExceptionにして、呼び出し側がそのレースだけ諦められるようにする。
    /// </summary>
    internal sealed class DownloadWait
    {
        private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(500);

        private readonly TimeSpan _limit;
        private TimeSpan _waited = TimeSpan.Zero;

        public DownloadWait(TimeSpan limit)
        {
            _limit = limit;
        }

        public void Wait()
        {
            _waited += Step;
            if (_waited > _limit)
                throw new TimeoutException($"ダウンロード待ちが{_limit.TotalSeconds:0}秒を超えました。");
            Thread.Sleep(Step);
        }

        /// <summary>データが読めた（-3以外が返った）ら、待ち時間を数え直す。</summary>
        public void Reset()
        {
            _waited = TimeSpan.Zero;
        }
    }
}
