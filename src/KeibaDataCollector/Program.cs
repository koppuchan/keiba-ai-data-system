using System;
using KeibaDataCollector.Data;
using KeibaDataCollector.Models;

namespace KeibaDataCollector
{
    /// <summary>
    /// Phase 1（Issue #1）時点では licensegate コマンドのみ。
    /// morning/watch/predict/probe 等の既存コマンド群はPhase 2（Issue #2）でSource Adapterと
    /// 一緒に合流する。管理画面はまだ無いため、当面はこのCLIがLicenseGateの唯一の操作口になる。
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] != "licensegate")
            {
                PrintUsage();
                return 1;
            }

            using (var store = new LicenseGateStore(AppConfig.HistoricalDbPath))
            {
                try
                {
                    return RunLicenseGateCommand(store, args);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ERROR] {ex.Message}");
                    return 1;
                }
            }
        }

        private static int RunLicenseGateCommand(LicenseGateStore store, string[] args)
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return 1;
            }

            switch (args[1])
            {
                case "show":
                    ShowStatus(store);
                    return 0;

                case "set-jra":
                    // licensegate set-jra <active|inactive> <approved|pending|rejected> <approved|pending|rejected> [note]
                    if (args.Length < 5) { PrintUsage(); return 1; }
                    store.SetJraLicense(new JraLicenseState
                    {
                        AcquisitionState = ParseEnum<AcquisitionState>(args[2]),
                        WebPublishApproval = ParseEnum<ApprovalState>(args[3]),
                        CommercialUseApproval = ParseEnum<ApprovalState>(args[4]),
                    }, note: args.Length > 5 ? args[5] : null);
                    Console.WriteLine("[licensegate] JRA許諾状態を更新しました。");
                    ShowStatus(store);
                    return 0;

                case "set-local":
                    // licensegate set-local <venueId> <venueName> <active|inactive> <approved|pending|rejected> [note]
                    if (args.Length < 5) { PrintUsage(); return 1; }
                    store.SetLocalVenueLicense(new LocalVenueLicenseState
                    {
                        VenueId = args[2],
                        VenueName = args[3],
                        ContractState = ParseEnum<AcquisitionState>(args[4]),
                        WebPublishApproval = args.Length > 5 ? ParseEnum<ApprovalState>(args[5]) : ApprovalState.Pending,
                    }, note: args.Length > 6 ? args[6] : null);
                    Console.WriteLine($"[licensegate] 地方競馬 venue={args[2]} の許諾状態を更新しました。");
                    ShowStatus(store);
                    return 0;

                case "check":
                    // licensegate check <venueId> <central|local>
                    if (args.Length < 4) { PrintUsage(); return 1; }
                    var isCentral = args[3] == "central";
                    var allowed = store.IsWebPublishAllowed(args[2], isCentral);
                    Console.WriteLine($"[licensegate] venue={args[2]} ({(isCentral ? "中央" : "地方")}) Web公開: {(allowed ? "許可" : "停止")}");
                    return 0;

                default:
                    PrintUsage();
                    return 1;
            }
        }

        private static void ShowStatus(LicenseGateStore store)
        {
            var jra = store.GetJraLicense();
            Console.WriteLine("=== JRA（中央競馬） ===");
            Console.WriteLine(jra == null
                ? "  未設定（＝全レース非公開）"
                : $"  取得:{jra.AcquisitionState} / Web公開許諾:{jra.WebPublishApproval} / 商用利用許諾:{jra.CommercialUseApproval} (更新:{jra.UpdatedAtUtc:u})");

            Console.WriteLine("=== 地方競馬（venue別） ===");
            var locals = store.ListLocalVenueLicenses();
            if (locals.Count == 0)
            {
                Console.WriteLine("  登録なし（＝全場非公開）");
                return;
            }
            foreach (var v in locals)
            {
                Console.WriteLine($"  {v.VenueId} ({v.VenueName}): 契約:{v.ContractState} / Web公開許諾:{v.WebPublishApproval} (更新:{v.UpdatedAtUtc:u})");
            }
        }

        private static T ParseEnum<T>(string value) where T : struct =>
            (T)Enum.Parse(typeof(T), value, ignoreCase: true);

        private static void PrintUsage()
        {
            Console.WriteLine("使い方:");
            Console.WriteLine("  KeibaDataCollector.exe licensegate show");
            Console.WriteLine("  KeibaDataCollector.exe licensegate set-jra <active|inactive> <approved|pending|rejected> <approved|pending|rejected> [note]");
            Console.WriteLine("  KeibaDataCollector.exe licensegate set-local <venueId> <venueName> <active|inactive> <approved|pending|rejected> [note]");
            Console.WriteLine("  KeibaDataCollector.exe licensegate check <venueId> <central|local>");
        }
    }
}
