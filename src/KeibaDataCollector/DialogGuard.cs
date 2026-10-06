using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace KeibaDataCollector
{
    /// <summary>
    /// JV-Link/UmaConnが出すメッセージボックス（例: 「サーバメンテナンス中です。」）を自動で閉じる。
    ///
    /// これらはモーダルダイアログのため、誰も押さないとJVOpen等の呼び出しが返ってこない。
    /// 無人のVPSではそのまま止まり続け、タスクの多重起動禁止（IgnoreNew）と
    /// COMのロック（ComAccessLock）により、以降の実行がすべて待たされてしまう。
    ///
    /// 安全のため、自分のプロセスが出したOKボタンだけのダイアログに限る。
    /// 選択肢のあるダイアログ（はい/いいえ等）は勝手に答えず、内容を出力するだけにする。
    /// </summary>
    internal static class DialogGuard
    {
        private const uint BM_CLICK = 0x00F5;
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public static void Start()
        {
            var ownPid = (uint)Process.GetCurrentProcess().Id;
            new Thread(() =>
            {
                while (true)
                {
                    try { EnumWindows((hWnd, _) => { Inspect(hWnd, ownPid); return true; }, IntPtr.Zero); }
                    catch { /* 監視の失敗で本処理を止めない */ }
                    Thread.Sleep(PollInterval);
                }
            })
            { IsBackground = true, Name = "dialog-guard" }.Start();
        }

        private static void Inspect(IntPtr hWnd, uint ownPid)
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid != ownPid || !IsWindowVisible(hWnd)) return;
            if (ClassOf(hWnd) != "#32770") return; // 標準のダイアログ/メッセージボックスのみ

            var title = TextOf(hWnd);
            var body = new StringBuilder();
            var buttons = new System.Collections.Generic.List<IntPtr>();
            EnumChildWindows(hWnd, (child, _) =>
            {
                var cls = ClassOf(child);
                if (cls == "Static")
                {
                    var t = TextOf(child);
                    if (t.Length > 0) body.Append(t).Append(' ');
                }
                else if (cls == "Button")
                {
                    buttons.Add(child);
                }
                return true;
            }, IntPtr.Zero);

            var message = $"「{title}」{body.ToString().Trim()}";
            if (buttons.Count == 1)
            {
                Console.WriteLine($"[dialog] JV-Link/UmaConnのメッセージを自動で閉じました: {message}");
                PostMessage(buttons[0], BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                Console.WriteLine($"[dialog] 選択肢のあるダイアログが表示されています（自動では閉じません）: {message}");
            }
        }

        private static string ClassOf(IntPtr hWnd)
        {
            var sb = new StringBuilder(64);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string TextOf(IntPtr hWnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
