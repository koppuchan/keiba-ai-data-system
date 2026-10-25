using System;
using System.Threading;

namespace KeibaDataCollector
{
    /// <summary>
    /// JV-Link/UmaConnを使う処理を、ソースごとに同時に1つだけにする（プロセス間ロック）。
    /// JV-Link（中央競馬）とUmaConn（地方競馬）は別々のCOMで別々のサーバーなので、ロックも別にして
    /// 片方が固まってももう片方を止めないようにする（以前は1つのロックで、JV-Linkの停止が地方競馬の
    /// 更新まで止めた）。両方を使う処理（検証・取得など）は、JV→UmaConnの順で両方を取る。
    /// 旧システムのタスクとは名前が異なるため干渉しない（旧システム側は変更していない）。
    /// </summary>
    internal sealed class ComAccessLock : IDisposable
    {
        public const string Jv = "JV";
        public const string Uma = "NV";

        private readonly System.Collections.Generic.List<Mutex> _held;

        private ComAccessLock(System.Collections.Generic.List<Mutex> held) { _held = held; }

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
                case "cycle":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>他の処理が終わるまで最大 wait だけ待つ。取れなければnull（今回は見送り）。
        /// 複数を指定したときは先頭から順に取り、1つでも取れなければ取った分を手放す。</summary>
        public static ComAccessLock TryAcquire(TimeSpan wait, params string[] names)
        {
            var held = new System.Collections.Generic.List<Mutex>();
            foreach (var name in names)
            {
                var mutex = new Mutex(false, @"Global\KeibaAiDataSystem-ComAccess-" + name);
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

                if (!acquired)
                {
                    mutex.Dispose();
                    foreach (var h in held) { try { h.ReleaseMutex(); } catch (ApplicationException) { } h.Dispose(); }
                    return null;
                }
                held.Add(mutex);
            }
            return new ComAccessLock(held);
        }

        public void Dispose()
        {
            if (_held == null) return;
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                try { _held[i].ReleaseMutex(); } catch (ApplicationException) { }
                _held[i].Dispose();
            }
            _held.Clear();
        }
    }
}
