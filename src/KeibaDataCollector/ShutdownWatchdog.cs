using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeibaDataCollector
{
    /// <summary>
    /// 作業が終わったあと、プロセスが確実に終了することを保証する。
    ///
    /// 実際に発生した障害（2026-08-12）:
    ///   predictモードは9:00に全レースを処理し「予想の反映完了」まで出力したのに、
    ///   プロセスが終了しなかった。バッチのログに "predict batch end" が出ていない。
    ///   多重起動を禁止（IgnoreNew）しているため、以降30分ごとの実行がすべて
    ///   0x800710E0（要求が拒否されました）で弾かれ、その日は9:00の1回しか動かなかった。
    ///   9:00は発売前でオッズが無いため、結果として終日predictが0件になった。
    ///
    /// 原因はCOMの後片付け。READMEに「UmaConn終了時のメモリリークダイアログ」を
    /// 未解決として記録していたとおり、解放時にモーダルダイアログが出ると
    /// 誰も操作できないサーバー上では永久に待ち続ける。
    ///
    /// 作業自体は完了しているので、後片付けが固まってもプロセスは終わらせてよい。
    /// 終わらせないほうが害が大きい（翌回以降が全部動かなくなる）。
    /// </summary>
    internal static class ShutdownWatchdog
    {
        /// <summary>後片付けにこれだけ待ってやり、過ぎたら強制的に終了させる。</summary>
        private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(30);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

        /// <summary>OSにプロセスを即時終了させる（.NETの終了処理やCOMの後片付けを一切待たない）。
        /// 失敗した場合だけ Process.Kill にフォールバックする。</summary>
        private static void HardExit(int exitCode)
        {
            try { TerminateProcess(GetCurrentProcess(), (uint)exitCode); } catch { }
            Process.GetCurrentProcess().Kill();
        }

        /// <summary>Environment.Exit 自体も固まる場合に備えた二段目の猶予。</summary>
        private static readonly TimeSpan HardKillAfter = TimeSpan.FromSeconds(10);

        /// <summary>
        /// 開始時に呼ぶ。作業そのものが（COMの応答待ちなどで）終わらない場合に、制限時間で
        /// プロセスを終了させる。終了コードは1（異常）。COMのロック（ComAccessLock）はプロセス終了で解放される。
        /// </summary>
        public static void ArmRunLimit(TimeSpan limit)
        {
            new Thread(() =>
            {
                Thread.Sleep(limit);

                // メッセージの出力自体が固まっても終了できるよう、先に別スレッドで強制終了を仕掛けておく。
                new Thread(() => { Thread.Sleep(HardKillAfter); HardExit(1); }) { IsBackground = true }.Start();

                Console.WriteLine($"[watchdog] 処理が{limit.TotalMinutes:0}分以内に終わらなかったため、プロセスを終了します。" +
                                  "（JV-Link/UmaConnの応答待ちが原因の可能性）");
                Console.Out.Flush();
                // Environment.Exitを試さず、直接プロセスを終了させる。COM呼び出しで固まった状態では
                // Exit/Killが効かず、プロセスが何時間も残ったことがあった（実測 2026-10-13）。
                HardExit(1);
            })
            {
                IsBackground = true,
                Name = "run-limit-watchdog",
            }.Start();
        }

        /// <summary>
        /// 作業完了後に呼ぶ。正常に終了できればこの監視は何もしない
        /// （バックグラウンドスレッドなのでプロセス終了を妨げない）。
        /// </summary>
        public static void Arm(int exitCode)
        {
            var thread = new Thread(() =>
            {
                Thread.Sleep(GracePeriod);

                // ここに到達した＝猶予を過ぎても終了していない。
                Console.WriteLine(
                    $"[watchdog] 後片付けが{GracePeriod.TotalSeconds:0}秒で終わらないため、プロセスを終了します。" +
                    "（COM解放時のダイアログ等が原因。作業自体は完了しています）");
                Console.Out.Flush();

                // まずは終了コードを保ったまま終わらせる。
                new Thread(() => Environment.Exit(exitCode)) { IsBackground = true }.Start();

                // Environment.Exit も終了ハンドラで固まりうるので、最後は問答無用で落とす。
                Thread.Sleep(HardKillAfter);
                HardExit(exitCode);
            })
            {
                IsBackground = true,
                Name = "shutdown-watchdog",
            };
            thread.Start();
        }
    }
}
