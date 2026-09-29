using System;
using System.Linq;
using System.Threading;
using KeibaDataCollector.Data;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using KeibaDataCollector.Services;
using KeibaDataCollector.WordPress;

namespace KeibaDataCollector
{
    internal static class Program
    {
        // 異常があったかどうか。タスクスケジューラの「前回の実行結果」に反映させる。
        // これが常に0だと、1日分まるごと反映されていなくても「成功」に見えてしまい、
        // お客様からの指摘で初めて気づくことになる（keiba-race-result-auto-postingで実際に発生した）。
        private static bool _hadFailure;

        // COM(ActiveX)相手はSTAスレッドが前提のため必須。
        [STAThread]
        private static int Main(string[] args)
        {
            var mode = args.Length > 0 ? args[0] : "help";
            // probe のみ第2引数でレースキーを受け取る（例: probe 20260811-46-1R）。
            var arg = args.Length > 1 ? args[1] : null;

            try
            {
                Run(mode, arg, args);
            }
            catch (Exception ex)
            {
                // ここまで漏れてくるのは設定不備など、処理を始める前の失敗。
                // 未処理例外のまま落とすと、サーバーではWindowsのエラー報告ダイアログが
                // 出てタスクが終了しなくなる恐れがあるため、必ず捕まえて終了コードで返す。
                LogFailure("起動", "処理を開始できませんでした", ex);
            }

            if (_hadFailure)
            {
                Console.WriteLine("異常終了: 上記のエラーを確認してください。");
                return 1;
            }
            return 0;
        }

        private static void Run(string mode, string arg = null, string[] args = null)
        {
            args = args ?? new string[0];

            // licensegate/weights はJV-Link/UmaConnのCOM初期化を必要としないため、他のモードより先に分岐する。
            // サブコマンド解析側はmode(args[0])を含まない配列を期待するため、先頭要素を除いて渡す。
            var subArgs = args.Length > 0 ? args.Skip(1).ToArray() : args;

            if (mode == "licensegate")
            {
                using (var store = new LicenseGateStore(AppConfig.HistoricalDbPath))
                {
                    RunLicenseGateCommand(store, subArgs);
                }
                return;
            }
            if (mode == "weights")
            {
                using (var historical = new HistoricalDataStore(AppConfig.HistoricalDbPath))
                using (var scoresStore = new ScoresStore(historical.Connection))
                {
                    RunWeightsCommand(scoresStore, subArgs);
                }
                return;
            }

            // WordPressClient はここでは作らない: setup モードはWordPressに一切繋がないため、
            // WordPressUser/WordPressAppPassword 未設定でも setup だけは実行できるようにする。
            using (var jvLink = new JvSpecComDataSource(AppConfig.JvLinkProgId, "JV", "JV-Link(中央競馬)"))
            using (var umaConn = new JvSpecComDataSource(AppConfig.UmaConnProgId, "NV", "UmaConn(地方競馬)"))
            try
            {
                switch (mode)
                {
                    case "morning":
                    {
                        var wp = new WordPressClient(
                            AppConfig.WordPressBaseUrl,
                            AppConfig.WordPressUser,
                            AppConfig.WordPressAppPassword);

                        // 片方のソース（例: UmaConn未設置）が失敗しても、もう片方は必ず動くように
                        // ソースごとに独立してtry/catchする。
                        RunMorningFor(jvLink, wp);
                        RunMorningFor(umaConn, wp);
                        break;
                    }

                    case "watch":
                    {
                        var wp = new WordPressClient(
                            AppConfig.WordPressBaseUrl,
                            AppConfig.WordPressUser,
                            AppConfig.WordPressAppPassword);

                        using (var cts = new CancellationTokenSource())
                        {
                            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

                            // ソースごとに独立してtry/catchし、片方の失敗がもう片方の監視を止めない
                            // ようにする。
                            var jvResultTask = RunWatchFor(jvLink, wp, cts.Token);
                            var umaResultTask = RunWatchFor(umaConn, wp, cts.Token);

                            try
                            {
                                System.Threading.Tasks.Task.WaitAll(jvResultTask, umaResultTask);
                            }
                            catch (AggregateException ex)
                            {
                                // Ctrl+C 時の Task.Delay 由来のキャンセルは正常系。
                                // それ以外は握りつぶさず記録する。
                                foreach (var inner in ex.Flatten().InnerExceptions)
                                {
                                    if (inner is OperationCanceledException) continue;
                                    LogFailure("watch", "監視タスクが異常終了しました", inner);
                                }
                            }
                        }
                        break;
                    }

                    case "predict":
                    {
                        // 朝一オッズの人気順から予想印（◎○▲△）を生成して反映する。
                        // 結果監視とは独立して動くため、片方が失敗しても他方に影響しない。
                        var wp = new WordPressClient(
                            AppConfig.WordPressBaseUrl,
                            AppConfig.WordPressUser,
                            AppConfig.WordPressAppPassword);

                        RunPredictFor(jvLink, wp);
                        RunPredictFor(umaConn, wp);
                        break;
                    }

                    case "score":
                    {
                        // 当日出走馬の6ファクターを算出しWordPress(hrc_factors)へ反映する。
                        // ローカルSQLite（backfill済みの履歴）を読むだけで、JV-Link/UmaConnからは
                        // 当日の出走表（KettoNum突き合わせ用）のみ取得する。
                        var targetDate = DateTime.Today;
                        foreach (var a in args)
                        {
                            if (DateTime.TryParseExact(a, "yyyy-MM-dd",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var parsed))
                            {
                                targetDate = parsed;
                                break;
                            }
                        }
                        if (targetDate != DateTime.Today)
                            Console.WriteLine($"対象日: {targetDate:yyyy-MM-dd}（引数指定）");

                        var wp = new WordPressClient(
                            AppConfig.WordPressBaseUrl,
                            AppConfig.WordPressUser,
                            AppConfig.WordPressAppPassword);

                        using (var store = new HistoricalDataStore(AppConfig.HistoricalDbPath))
                        using (var scoresStore = new ScoresStore(store.Connection))
                        {
                            var scoring = new FactorScoringService(store);
                            RunScoreFor(jvLink, wp, scoring, scoresStore, targetDate);
                            RunScoreFor(umaConn, wp, scoring, scoresStore, targetDate);
                        }
                        break;
                    }

                    case "probe":
                        // 調査用。どのデータ種別で何が取得できるかを実際に叩いて確認する。
                        // WordPressには一切書き込まない。
                        RunProbeFor(jvLink, arg);
                        RunProbeFor(umaConn, arg);
                        break;

                    case "setup":
                        // 初回1回だけ手動実行: 利用キー入力ダイアログを開いて設定を保存する。
                        RunSetupFor(jvLink);
                        RunSetupFor(umaConn);
                        break;

                    case "backfill":
                    {
                        // 6ファクター用の履歴取得。WordPressには書き込まない
                        // （ローカルSQLiteに蓄積するだけ）。
                        var isIncremental = Array.IndexOf(args, "incremental") >= 0;
                        var sourceArg = Array.Find(args, a => a == "jv" || a == "uma");
                        var targetJv = sourceArg == null || sourceArg == "jv";
                        var targetUma = sourceArg == null || sourceArg == "uma";

                        var backfillOption = isIncremental ? DataOption.Normal : DataOption.Setup;
                        Console.WriteLine(isIncremental
                            ? "差分モード(option=Normal)で取得します。ダイアログは表示されません。"
                            : "全履歴モード(option=Setup)で取得します。スタートキット確認ダイアログが"
                              + "データ種別ごとに表示されるため、手動で応答してください"
                              + "（無人実行する場合は引数に incremental を付けてください）。");

                        using (var store = new HistoricalDataStore(AppConfig.HistoricalDbPath))
                        {
                            if (targetJv) RunBackfillFor(jvLink, store, backfillOption);
                            if (targetUma) RunBackfillFor(umaConn, store, backfillOption);
                        }
                        break;
                    }

                    case "dbstats":
                        // backfill済みのSQLiteの中身を件数・日付範囲で確認する。COMもJV-Link/UmaConnも
                        // 使わないため、setup/backfillと違って一瞬で終わる。
                        using (var store = new HistoricalDataStore(AppConfig.HistoricalDbPath))
                        {
                            store.PrintStats();
                        }
                        break;

                    case "trend":
                    {
                        // trend <morning|live|final> [yyyy-MM-dd]
                        // args[0]は"trend"自身（modeトークン）のため、ステージは args[1] を見る
                        // （licensegate/weightsと同じ落とし穴を踏まないよう、ここは明示的にargs[1]を使う）。
                        if (args.Length < 2 || !Enum.TryParse<TrendStage>(args[1], ignoreCase: true, out var stage))
                        {
                            Console.WriteLine("使い方: KeibaDataCollector.exe trend <morning|live|final> [yyyy-MM-dd]");
                            break;
                        }
                        var targetDate = DateTime.Today;
                        if (args.Length > 2 && DateTime.TryParseExact(args[2], "yyyy-MM-dd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var parsed))
                        {
                            targetDate = parsed;
                        }

                        using (var store = new HistoricalDataStore(AppConfig.HistoricalDbPath))
                        using (var trendStore = new TrendStore(store.Connection))
                        {
                            RunTrendFor(jvLink, store, trendStore, targetDate, stage);
                            RunTrendFor(umaConn, store, trendStore, targetDate, stage);
                        }
                        break;
                    }

                    case "content":
                    {
                        // content [yyyy-MM-dd]
                        var targetDate = DateTime.Today;
                        if (args.Length > 1 && DateTime.TryParseExact(args[1], "yyyy-MM-dd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var parsed))
                        {
                            targetDate = parsed;
                        }

                        using (var store = new HistoricalDataStore(AppConfig.HistoricalDbPath))
                        using (var scoresStore = new ScoresStore(store.Connection))
                        {
                            RunContentFor(jvLink, scoresStore, targetDate);
                            RunContentFor(umaConn, scoresStore, targetDate);
                        }
                        break;
                    }

                    default:
                        PrintUsage();
                        break;
                }
            }
            finally
            {
                // ここまで来れば作業は終わっている。この先はCOMの後片付けだけで、
                // そこが固まってもプロセスは終了させてよい（終了しないほうが害が大きい）。
                ShutdownWatchdog.Arm(_hadFailure ? 1 : 0);
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("使い方: KeibaDataCollector.exe [setup|morning|predict|score|watch|probe|backfill|dbstats|trend|content|licensegate|weights]");
            Console.WriteLine("  setup       : 初回のみ。利用キー等をGUIダイアログで設定する。");
            Console.WriteLine("  morning     : 朝一バッチ。当日の出走表を取得しWordPressへ反映する。");
            Console.WriteLine("  predict     : 朝一オッズの人気順から予想印を生成しWordPressへ反映する。");
            Console.WriteLine("  score       : 当日出走馬の6ファクターを算出しWordPress(hrc_factors)へ反映する。");
            Console.WriteLine("              事前にbackfillで履歴を蓄積しておく必要がある。");
            Console.WriteLine("              引数省略時は今日。yyyy-MM-dd形式の日付を渡すとその日を対象にする。");
            Console.WriteLine("  watch       : レース確定を監視し、結果・払戻を随時WordPressへ反映する。");
            Console.WriteLine("  probe       : 調査用。どのデータ種別で何が取得できるか確認する（WordPressへは書き込まない）。");
            Console.WriteLine("              レースを指定する場合: probe 20260811-46-1R");
            Console.WriteLine("  backfill    : 6ファクター用の過去データ取得（先にprobe推奨）。");
            Console.WriteLine("              引数なし: 全履歴(option=Setup)。手動実行専用。");
            Console.WriteLine("              incremental: 差分のみ(option=Normal)。無人実行可。");
            Console.WriteLine("              ソースを絞る場合: backfill jv / backfill uma");
            Console.WriteLine("  dbstats     : backfillで蓄積したSQLiteの件数・日付範囲を確認する。");
            Console.WriteLine("  trend       : 本日の傾向（脚質・枠・馬場・上がり・通過順）を算出する。");
            Console.WriteLine("              trend <morning|live|final> [yyyy-MM-dd]");
            Console.WriteLine("              morning=過去データ+当日確定情報、live=開催中の当日結果逐次、final=終了後の全当日結果。");
            Console.WriteLine("  content     : 本日の狙い馬・穴馬・危険な人気馬を生成する（要:事前のscore実行）。");
            Console.WriteLine("              content [yyyy-MM-dd]");
            Console.WriteLine("  licensegate : LicenseGate（公開許諾状態）の確認・更新。詳細は `licensegate` (引数なし) 実行。");
            Console.WriteLine("  weights     : AI指数6ファクターの重み設定の確認・更新。詳細は `weights` (引数なし) 実行。");
        }

        /// <summary>例外の内容をログに残す。原因調査には型と発生箇所が要るため、
        /// Messageだけでなく例外の全文（スタックトレース含む）を出す。</summary>
        private static void LogFailure(string sourceName, string what, Exception ex)
        {
            _hadFailure = true;
            Console.WriteLine($"[{sourceName}] {what}: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.ToString());
        }

        private static void RunProbeFor(JvSpecComDataSource source, string raceKeySlug = null)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                new DataSpecProbeService(source).Run(DateTime.Today, raceKeySlug);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{source.SourceName}] 調査失敗（このソースのみスキップ）: {ex.Message}");
            }
        }

        /// <summary>4種類のバックフィル（RACE/SLOP/WOOD/BLOD）を1ソース分まとめて実行する。
        /// それぞれ独立してtry/catchする: 例えば血統(BLOD)がこの契約では提供されていない場合でも、
        /// レース履歴(RACE)や調教データだけは取り込めるようにするため。</summary>
        private static void RunBackfillFor(JvSpecComDataSource source, HistoricalDataStore store,
            DataOption dataOption)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
            }
            catch (Exception ex)
            {
                LogFailure(source.SourceName, "バックフィル初期化失敗（このソースをスキップ）", ex);
                return;
            }

            var backfill = new BackfillService(source, store, dataOption);

            RunOneBackfillStep(source.SourceName, "RACE(レース履歴)", backfill.BackfillRaceEntries);
            RunOneBackfillStep(source.SourceName, "SLOP(坂路調教)", backfill.BackfillSlopeTraining);
            RunOneBackfillStep(source.SourceName, "WOOD(ウッドチップ調教)", backfill.BackfillWoodChipTraining);
            RunOneBackfillStep(source.SourceName, "BLOD(血統)", backfill.BackfillPedigree);
        }

        private static void RunOneBackfillStep(string sourceName, string stepName, Action step)
        {
            Console.WriteLine($"[{sourceName}] {stepName} バックフィル開始: {DateTime.Now:HH:mm:ss}");
            try
            {
                step();
            }
            catch (Exception ex)
            {
                LogFailure(sourceName, $"{stepName} バックフィル失敗（この種別のみスキップして続行）", ex);
            }
            Console.WriteLine($"[{sourceName}] {stepName} バックフィル終了: {DateTime.Now:HH:mm:ss}");
        }

        private static void RunSetupFor(JvSpecComDataSource source)
        {
            Console.WriteLine($"[{source.SourceName}] セットアップダイアログを開きます...");
            try
            {
                source.RunInteractiveSetup();
                Console.WriteLine($"[{source.SourceName}] セットアップ完了。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{source.SourceName}] セットアップ失敗: {ex.Message}");
            }
        }

        private static void RunMorningFor(JvSpecComDataSource source, WordPressClient wp)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                new RaceCardService(source, wp).RunMorningBatch(DateTime.Today, trackCode: "");
            }
            catch (Exception ex)
            {
                // 片方のソースが失敗しても、もう片方は動かす。ただし失敗は終了コードに残す。
                LogFailure(source.SourceName, "朝一バッチ失敗（このソースのみスキップして続行）", ex);
            }
        }

        private static void RunPredictFor(JvSpecComDataSource source, WordPressClient wp)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                new PredictionService(source, wp)
                    .RunAsync(DateTime.Today, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LogFailure(source.SourceName, "予想の生成に失敗（このソースのみスキップして続行）", ex);
            }
        }

        private static void RunScoreFor(JvSpecComDataSource source, WordPressClient wp, FactorScoringService scoring, ScoresStore scoresStore, DateTime targetDate)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                new FactorPublishService(source, wp, scoring, scoresStore).RunForToday(targetDate);
            }
            catch (Exception ex)
            {
                LogFailure(source.SourceName, "6ファクター算出に失敗（このソースのみスキップして続行）", ex);
            }
        }

        private static void RunTrendFor(JvSpecComDataSource source, HistoricalDataStore historical, TrendStore trendStore, DateTime targetDate, TrendStage stage)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);

                // 当日この開催場が無いソース（例: 地方開催が無い日のJV-Link）は0件で正常に終わる。
                var venues = RaceDiscovery.ForDate(source, targetDate)
                    .Select(k => k.TrackCode).Distinct().ToList();
                if (venues.Count == 0)
                {
                    Console.WriteLine($"[{source.SourceName}] {targetDate:yyyy-MM-dd} 該当開催場なし。");
                    return;
                }

                var engine = new TrendEngineService(source, historical, trendStore);
                foreach (var trackCode in venues)
                {
                    var snapshot = engine.ComputeAndSave(targetDate, trackCode, stage);
                    Console.WriteLine(
                        $"[{source.SourceName}] {targetDate:yyyy-MM-dd} 場={trackCode} 傾向({stage}) 算出完了: " +
                        $"対象レース数={snapshot.RacesConsidered}, 脚質サンプル={snapshot.Pace.SampleCount}, " +
                        $"枠サンプル合計={snapshot.PostPosition.ByWaku.Values.Sum(w => w.SampleCount)}, " +
                        $"上がりサンプル={snapshot.Agari.SampleCount}, 通過順サンプル={snapshot.Passage.SampleCount}");
                }
            }
            catch (Exception ex)
            {
                LogFailure(source.SourceName, "本日の傾向の算出に失敗（このソースのみスキップして続行）", ex);
            }
        }

        private static void RunContentFor(JvSpecComDataSource source, ScoresStore scoresStore, DateTime targetDate)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);

                var venues = RaceDiscovery.ForDate(source, targetDate)
                    .Select(k => k.TrackCode).Distinct().ToList();
                if (venues.Count == 0)
                {
                    Console.WriteLine($"[{source.SourceName}] {targetDate:yyyy-MM-dd} 該当開催場なし。");
                    return;
                }

                var generator = new ContentGeneratorService(source, scoresStore);
                foreach (var trackCode in venues)
                {
                    var picks = generator.GenerateForVenue(targetDate, trackCode);
                    Console.WriteLine($"[{source.SourceName}] {targetDate:yyyy-MM-dd} 場={trackCode} " +
                        $"狙い馬={picks.Count(p => p.Category == PickCategory.Nerai)}件 " +
                        $"穴馬={picks.Count(p => p.Category == PickCategory.Ana)}件 " +
                        $"危険な人気馬={picks.Count(p => p.Category == PickCategory.Kiken)}件");
                    foreach (var pick in picks)
                        Console.WriteLine($"    [{pick.Category}] {pick.Text}");
                }
            }
            catch (Exception ex)
            {
                LogFailure(source.SourceName, "本日の狙い馬・穴馬・危険な人気馬の生成に失敗（このソースのみスキップして続行）", ex);
            }
        }

        // 監視が例外で落ちたときの再開待ち時間。
        private static readonly TimeSpan WatchRetryDelay = TimeSpan.FromMinutes(3);

        /// <summary>
        /// 1つのデータ源の監視を、その日の打ち切り時刻まで動かし続ける。
        /// 例外を1回捕まえただけでその日の監視を諦めない。間隔をあけて再開し、最後まで粘る。
        /// </summary>
        private static async System.Threading.Tasks.Task RunWatchFor(
            JvSpecComDataSource source, WordPressClient wp, CancellationToken ct)
        {
            var attempt = 0;

            while (!ct.IsCancellationRequested)
            {
                attempt++;
                try
                {
                    source.Initialize(AppConfig.JvLinkSoftwareId);
                    await new RaceResultService(source, wp, AppConfig.RealtimePollInterval)
                        .RunWatchLoopAsync(DateTime.Today, ct);

                    // 打ち切り時刻まで動ききった＝その日の監視は完了。
                    return;
                }
                catch (OperationCanceledException)
                {
                    return; // Ctrl+C / 停止要求。異常ではない。
                }
                catch (Exception ex)
                {
                    LogFailure(source.SourceName, $"監視が中断しました（{attempt}回目）", ex);
                }

                // 打ち切り時刻を過ぎていれば再開しない（翌日のタスクを妨げないため）。
                if (DateTime.Now >= DateTime.Today.Add(RaceResultService.DailyCutoff))
                {
                    Console.WriteLine($"[{source.SourceName}] 本日の監視時間を過ぎたため再開しません。");
                    return;
                }

                Console.WriteLine(
                    $"[{source.SourceName}] {WatchRetryDelay.TotalMinutes:0}分後に監視を再開します。");
                try
                {
                    await System.Threading.Tasks.Task.Delay(WatchRetryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        // ---- licensegate ----

        private static void RunLicenseGateCommand(LicenseGateStore store, string[] args)
        {
            if (args.Length < 1)
            {
                PrintLicenseGateUsage();
                ShowLicenseGateStatus(store);
                return;
            }

            switch (args[0])
            {
                case "show":
                    ShowLicenseGateStatus(store);
                    return;

                case "set-jra":
                    // licensegate set-jra <active|inactive> <approved|pending|rejected> <approved|pending|rejected> [note]
                    if (args.Length < 4) { PrintLicenseGateUsage(); return; }
                    store.SetJraLicense(new JraLicenseState
                    {
                        AcquisitionState = ParseEnum<AcquisitionState>(args[1]),
                        WebPublishApproval = ParseEnum<ApprovalState>(args[2]),
                        CommercialUseApproval = ParseEnum<ApprovalState>(args[3]),
                    }, note: args.Length > 4 ? args[4] : null);
                    Console.WriteLine("[licensegate] JRA許諾状態を更新しました。");
                    ShowLicenseGateStatus(store);
                    return;

                case "set-local":
                    // licensegate set-local <venueId> <venueName> <active|inactive> <approved|pending|rejected> [note]
                    if (args.Length < 4) { PrintLicenseGateUsage(); return; }
                    store.SetLocalVenueLicense(new LocalVenueLicenseState
                    {
                        VenueId = args[1],
                        VenueName = args[2],
                        ContractState = ParseEnum<AcquisitionState>(args[3]),
                        WebPublishApproval = args.Length > 4 ? ParseEnum<ApprovalState>(args[4]) : ApprovalState.Pending,
                    }, note: args.Length > 5 ? args[5] : null);
                    Console.WriteLine($"[licensegate] 地方競馬 venue={args[1]} の許諾状態を更新しました。");
                    ShowLicenseGateStatus(store);
                    return;

                case "check":
                    // licensegate check <venueId> <central|local>
                    if (args.Length < 2) { PrintLicenseGateUsage(); return; }
                    var isCentral = args.Length > 2 && args[2] == "central";
                    var allowed = store.IsWebPublishAllowed(args[1], isCentral);
                    Console.WriteLine($"[licensegate] venue={args[1]} ({(isCentral ? "中央" : "地方")}) Web公開: {(allowed ? "許可" : "停止")}");
                    return;

                default:
                    PrintLicenseGateUsage();
                    return;
            }
        }

        private static void ShowLicenseGateStatus(LicenseGateStore store)
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

        private static void PrintLicenseGateUsage()
        {
            Console.WriteLine("使い方:");
            Console.WriteLine("  KeibaDataCollector.exe licensegate show");
            Console.WriteLine("  KeibaDataCollector.exe licensegate set-jra <active|inactive> <approved|pending|rejected> <approved|pending|rejected> [note]");
            Console.WriteLine("  KeibaDataCollector.exe licensegate set-local <venueId> <venueName> <active|inactive> <approved|pending|rejected> [note]");
            Console.WriteLine("  KeibaDataCollector.exe licensegate check <venueId> <central|local>");
        }

        // ---- weights（AI指数の重み設定。仕様書§8） ----

        private static void RunWeightsCommand(ScoresStore store, string[] args)
        {
            if (args.Length < 1)
            {
                PrintWeightsUsage();
                return;
            }

            switch (args[0])
            {
                case "show":
                    // segment省略時は "default" を見せる。
                    var segment = args.Length > 1 ? args[1] : ScoresStore.DefaultSegment;
                    var w = store.GetWeights(segment);
                    Console.WriteLine($"[weights] segment={segment} (フォールバック込み表示):");
                    Console.WriteLine($"  bias={w.WeightBias} pace={w.WeightPace} agariQ={w.WeightAgariQ} " +
                        $"jockeyRoi={w.WeightJockeyRoi} pedigreeFit={w.WeightPedigreeFit} trainingAcc={w.WeightTrainingAcc}");
                    return;

                case "set":
                    // weights set <segment> <bias> <pace> <agariQ> <jockeyRoi> <pedigreeFit> <trainingAcc>
                    // segment例: "default", "central:turf", "central:dirt", "local:turf", "local:dirt", "central", "local"
                    if (args.Length < 8) { PrintWeightsUsage(); return; }
                    store.SetWeights(new AiIndexWeights
                    {
                        Segment = args[1],
                        WeightBias = double.Parse(args[2]),
                        WeightPace = double.Parse(args[3]),
                        WeightAgariQ = double.Parse(args[4]),
                        WeightJockeyRoi = double.Parse(args[5]),
                        WeightPedigreeFit = double.Parse(args[6]),
                        WeightTrainingAcc = double.Parse(args[7]),
                    });
                    Console.WriteLine($"[weights] segment={args[1]} の重みを更新しました。");
                    return;

                default:
                    PrintWeightsUsage();
                    return;
            }
        }

        private static void PrintWeightsUsage()
        {
            Console.WriteLine("使い方:");
            Console.WriteLine("  KeibaDataCollector.exe weights show [segment]");
            Console.WriteLine("  KeibaDataCollector.exe weights set <segment> <bias> <pace> <agariQ> <jockeyRoi> <pedigreeFit> <trainingAcc>");
            Console.WriteLine("  segment例: default / central / local / central:turf / central:dirt / local:turf / local:dirt");
            Console.WriteLine("  未設定のsegmentを照会するとdefaultにフォールバックし、defaultも無ければ全項目1.0を返します。");
        }
    }
}
