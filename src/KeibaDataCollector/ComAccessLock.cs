using System;
using System.Threading;

namespace KeibaDataCollector
{
    /// <summary>
    /// JV-Link/UmaConnを使う処理を、このシステム内で同時に1つだけにする（プロセス間ロック）。
    /// 20分おきのscore/content、30分おきのtrendなどが同じ時刻に重なると、同じCOMと
    /// JRA-VAN/地方競馬DATAのサーバーへ同時に問い合わせることになり、-413（サーバーエラー）の
    /// 一因になる。順番待ちにして重ならないようにする。
    /// 旧システムのタスクとは名前が異なるため干渉しない（旧システム側は変更していない）。
    /// </summary>
    internal sealed class ComAccessLock : IDisposable
    {
        private const string MutexName = @"Global\KeibaAiDataSystem-ComAccess";

        private Mutex _mutex;

        private ComAccessLock(Mutex mutex) { _mutex = mutex; }

        /// <summary>COM（JV-Link/UmaConn）を使うモードか。watchは常駐してロックを握り続けてしまうため含めない。</summary>
        public static bool IsComMode(string mode)
        {
            switch (mode)
            {
                case "morning":
                case "predict":
                case "score":
                case "probe":
                case "setup":
                case "backfill":
                case "trend":
                case "content":
                case "verify":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>他の処理が終わるまで最大 wait だけ待つ。取れなければnull（今回は見送り）。</summary>
        public static ComAccessLock TryAcquire(TimeSpan wait)
        {
            var mutex = new Mutex(false, MutexName);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(wait);
            }
            catch (AbandonedMutexException)
            {
                // 前の処理が強制終了された（ShutdownWatchdog等）。ロック自体は取れている。
                acquired = true;
            }

            if (acquired) return new ComAccessLock(mutex);
            mutex.Dispose();
            return null;
        }

        public void Dispose()
        {
            if (_mutex == null) return;
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
