using System;
using System.Collections.Generic;
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
                LogFailure("起動", "処理を開始できませんでした", ex, critical: true);
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
            if (mode == "backup")
            {
                try
                {
                    var dest = BackupService.Run(AppConfig.HistoricalDbPath, keep: 14);
                    Console.WriteLine($"[backup] バックアップを作成しました: {dest}");
                    LogSuccess("backup", "DBバックアップ", "正常終了");
                }
                catch (Exception ex)
                {
                    LogFailure("backup", "DBバックアップに失敗", ex, critical: true);
                }
                return;
            }
            if (mode == "stats")
            {
                using (var historical = new HistoricalDataStore(AppConfig.HistoricalDbPath))
                using (var verificationStore = new VerificationStore(historical.Connection))
                {
                    RunStatsCommand(verificationStore, subArgs);
                }
                return;
            }
            if (mode == "dashboard")
            {
                var targetDate = DateTime.Today;
                if (subArgs.Length > 0 && DateTime.TryParseExact(subArgs[0], "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
                {
                    targetDate = parsed;
                }

                using (var historical = new HistoricalDataStore(AppConfig.HistoricalDbPath))
                using (var licenseGateStore = new LicenseGateStore(historical.Connection))
                using (var auditLog = new AuditLogStore(historical.Connection))
                {
                    var wp = new WordPressClient(
                        AppConfig.WordPressBaseUrl,
                        AppConfig.WordPressUser,
                        AppConfig.WordPressAppPassword);
                    var monitoring = new MonitoringService(historical.Connection, licenseGateStore, auditLog, wp);
                    var snapshot = monitoring.BuildSnapshotAsync(targetDate).GetAwaiter().GetResult();
                    PrintDashboard(snapshot);

                    // 仕様書§17監視ダッシュボードはWordPress管理画面でも確認できるようにする
                    // （VPSにSSHできない関係者向け）。送信失敗はダッシュボード表示自体を止めない。
                    try
                    {
                        wp.PushStatusAsync(snapshot).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[dashboard] WordPressへのステータス送信に失敗しました（表示は上記の通り取得済み）: {ex.Message}");
                    }
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
                        // 当日出走馬の6ファクター・AI指数を算出しscoresテーブルへ保存する。
                        // WordPressへは送らない（既存システムが同じ投稿へ既にhrc_factorsを
                        // 送信しているため、二重公開を避けている。contentコマンドが公開する
                        // 新規のkeiba_digest投稿がこのscoresテーブルを参照する）。
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

                        using (var store = new HistoricalDataStore(AppConfig.HistoricalDbPath))
                        using (var scoresStore = new ScoresStore(store.Connection))
                        {
                            var scoring = new FactorScoringService(store);
                            RunScoreFor(jvLink, scoring, scoresStore, targetDate);
                            RunScoreFor(umaConn, scoring, scoresStore, targetDate);
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
                        var pedigreeOnly = Array.IndexOf(args, "pedigree") >= 0;
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
                            if (targetJv) RunBackfillFor(jvLink, store, backfillOption, pedigreeOnly);
                            if (targetUma) RunBackfillFor(umaConn, store, backfillOption, pedigreeOnly);
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
                        using (var licenseGateStore = new LicenseGateStore(store.Connection))
                        using (var predictionStore = new PredictionStore(store.Connection))
                        using (var trendStore = new TrendStore(store.Connection))
                        {
                            var validator = new ValidatorService(scoresStore, licenseGateStore, predictionStore);
                            var wp = new WordPressClient(
                                AppConfig.WordPressBaseUrl,
                                AppConfig.WordPressUser,
                                AppConfig.WordPressAppPassword);
                            var publisher = new DigestPublisherService(wp, licenseGateStore, predictionStore);
                            RunContentFor(jvLink, scoresStore, trendStore, validator, publisher, targetDate);
                            RunContentFor(umaConn, scoresStore, trendStore, validator, publisher, targetDate);
                        }
                        break;
                    }

                    case "verify":
                    {
                        // verify [yyyy-MM-dd]
                        var targetDate = DateTime.Today;
                        if (args.Length > 1 && DateTime.TryParseExact(args[1], "yyyy-MM-dd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var parsed))
                        {
                            targetDate = parsed;
                        }

                        using (var store = new HistoricalDataStore(AppConfig.HistoricalDbPath))
                        using (var predictionStore = new PredictionStore(store.Connection))
                        using (var verificationStore = new VerificationStore(store.Connection))
                        {
                            RunVerifyFor(jvLink, predictionStore, verificationStore, targetDate);
                            RunVerifyFor(umaConn, predictionStore, verificationStore, targetDate);
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
            Console.WriteLine("使い方: KeibaDataCollector.exe [setup|morning|predict|score|watch|probe|backfill|dbstats|trend|content|verify|backup|licensegate|weights|stats|dashboard]");
            Console.WriteLine("  setup       : 初回のみ。利用キー等をGUIダイアログで設定する。");
            Console.WriteLine("  morning     : 朝一バッチ。当日の出走表を取得しWordPressへ反映する。");
            Console.WriteLine("  predict     : 朝一オッズの人気順から予想印を生成しWordPressへ反映する。");
            Console.WriteLine("  score       : 当日出走馬の6ファクター・AI指数を算出しscoresテーブルへ保存する");
            Console.WriteLine("              （WordPressへは送らない。既存システムが同じ投稿へ既にhrc_factorsを");
            Console.WriteLine("              送信しているため）。contentコマンドがこのscoresテーブルを公開する。");
            Console.WriteLine("              事前にbackfillで履歴を蓄積しておく必要がある。");
            Console.WriteLine("              引数省略時は今日。yyyy-MM-dd形式の日付を渡すとその日を対象にする。");
            Console.WriteLine("  watch       : レース確定を監視し、結果・払戻を随時WordPressへ反映する。");
            Console.WriteLine("  probe       : 調査用。どのデータ種別で何が取得できるか確認する（WordPressへは書き込まない）。");
            Console.WriteLine("              レースを指定する場合: probe 20260811-46-1R");
            Console.WriteLine("  backfill    : 6ファクター用の過去データ取得（先にprobe推奨）。");
            Console.WriteLine("              引数なし: 全履歴(option=Setup)。手動実行専用。");
            Console.WriteLine("              incremental: 差分のみ(option=Normal)。無人実行可。");
            Console.WriteLine("              ソースを絞る場合: backfill jv / backfill uma");
            Console.WriteLine("              血統データだけ取り直す場合: backfill pedigree jv");
            Console.WriteLine("  dbstats     : backfillで蓄積したSQLiteの件数・日付範囲を確認する。");
            Console.WriteLine("  trend       : 本日の傾向（脚質・枠・馬場・上がり・通過順）を算出する。");
            Console.WriteLine("              trend <morning|live|final> [yyyy-MM-dd]");
            Console.WriteLine("              morning=過去データ+当日確定情報、live=開催中の当日結果逐次、final=終了後の全当日結果。");
            Console.WriteLine("  content     : 本日の狙い馬・穴馬・危険な人気馬を生成する（要:事前のscore実行）。");
            Console.WriteLine("              content [yyyy-MM-dd]");
            Console.WriteLine("  verify      : predictionsを確定着順と突き合わせてverificationへ記録する。");
            Console.WriteLine("              verify [yyyy-MM-dd]");
            Console.WriteLine("  backup      : historical.sqlite3のバックアップを作成する（直近14世代を保持）。");
            Console.WriteLine("  stats       : 指数帯別の3着内率・勝率を表示する（要:事前のverify実行）。");
            Console.WriteLine("              stats <Nerai|Ana|Kiken|AiIndexTop5> <modelVersion>");
            Console.WriteLine("  licensegate : LicenseGate（公開許諾状態）の確認・更新。詳細は `licensegate` (引数なし) 実行。");
            Console.WriteLine("  weights     : AI指数6ファクターの重み設定の確認・更新。詳細は `weights` (引数なし) 実行。");
            Console.WriteLine("  dashboard   : 監視ダッシュボード（仕様書§17）をコンソール表示し、WordPressへも送信する。");
            Console.WriteLine("              dashboard [yyyy-MM-dd]");
        }

        /// <summary>例外の内容をログに残す。原因調査には型と発生箇所が要るため、
        /// Messageだけでなく例外の全文（スタックトレース含む）を出す。</summary>
        private static void LogFailure(string sourceName, string what, Exception ex, bool critical = false)
        {
            _hadFailure = true;
            var summary = $"{what}: {ex.GetType().Name}: {ex.Message}";
            Console.WriteLine($"[{sourceName}] {summary}");
            Console.WriteLine(ex.ToString());

            // 仕様書§15「エラー処理」。ここまで来る例外はすべて監査ログへ記録する
            // （§17監視ダッシュボードの「エラー件数」の情報源）。個別のコマンドを手直しせず、
            // 全コマンドが最終的に通るこの1箇所から横断的に記録する。
            // criticalは「バッチ全体が始まらなかった」等、運用者が即座に気付くべき場合のみtrueにし、
            // それ以外（1レースだけの失敗等）はダッシュボードでの確認に留めてメール通知はしない
            // （通知のたびにメールが飛ぶと、日常的に起きる軽微なスキップで警報が形骸化するため）。
            try
            {
                using (var audit = new AuditLogStore(AppConfig.HistoricalDbPath))
                {
                    var notifier = new NotifierService(audit);
                    notifier.Notify(
                        critical ? NotifierService.SeverityCritical : NotifierService.SeverityError,
                        sourceName, "エラー", summary);
                }
            }
            catch (Exception auditEx)
            {
                // 監査ログ自体の書き込み失敗でバッチを止めない。
                Console.WriteLine($"[AuditLogStore] 監査ログの記録に失敗: {auditEx.Message}");
            }
        }

        /// <summary>成功時の監査ログ記録。MonitoringService（仕様書§17）がデータソースの
        /// 「直近のイベントは正常だったか」を判定できるよう、失敗だけでなく成功も記録する
        /// （LogFailureしか無いとaudit_logsが失敗しか含まなくなり、「エラーが無い」＝「正常」なのか
        /// 「そもそも一度も実行されていない」なのかを区別できなくなる）。</summary>
        private static void LogSuccess(string sourceName, string category, string message)
        {
            try
            {
                using (var audit = new AuditLogStore(AppConfig.HistoricalDbPath))
                {
                    audit.Log(NotifierService.SeverityInfo, sourceName, category, message);
                }
            }
            catch (Exception auditEx)
            {
                Console.WriteLine($"[AuditLogStore] 監査ログの記録に失敗: {auditEx.Message}");
            }
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
            DataOption dataOption, bool pedigreeOnly)
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

            if (!pedigreeOnly)
            {
                RunOneBackfillStep(source.SourceName, "RACE(レース履歴)", backfill.BackfillRaceEntries);
                RunOneBackfillStep(source.SourceName, "SLOP(坂路調教)", backfill.BackfillSlopeTraining);
                RunOneBackfillStep(source.SourceName, "WOOD(ウッドチップ調教)", backfill.BackfillWoodChipTraining);
            }
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
                LogSuccess(source.SourceName, "朝一バッチ", "正常終了");
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
                LogSuccess(source.SourceName, "予想生成", "正常終了");
            }
            catch (Exception ex)
            {
                LogFailure(source.SourceName, "予想の生成に失敗（このソースのみスキップして続行）", ex);
            }
        }

        private static void RunScoreFor(JvSpecComDataSource source, FactorScoringService scoring, ScoresStore scoresStore, DateTime targetDate)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                new FactorPublishService(source, scoring, scoresStore).RunForToday(targetDate);
                LogSuccess(source.SourceName, "AI指数算出", "正常終了");
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
                LogSuccess(source.SourceName, "傾向算出", $"正常終了（{stage}）");
            }
            catch (Exception ex)
            {
                LogFailure(source.SourceName, "本日の傾向の算出に失敗（このソースのみスキップして続行）", ex);
            }
        }

        private static void RunContentFor(JvSpecComDataSource source, ScoresStore scoresStore, TrendStore trendStore,
            ValidatorService validator, DigestPublisherService publisher, DateTime targetDate)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                var isCentral = source.SourceName.Contains("中央");

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

                    var validated = new List<(GeneratedPick Pick, ValidationOutcome Outcome)>();
                    foreach (var pick in picks)
                    {
                        var outcome = validator.ValidateAndSnapshot(pick, isCentral);
                        validated.Add((pick, outcome));
                        if (outcome.Passed)
                        {
                            Console.WriteLine($"    [OK] [{pick.Category}] {pick.Text}");
                        }
                        else
                        {
                            Console.WriteLine($"    [公開停止] [{pick.Category}] {pick.Text}");
                            foreach (var note in outcome.Notes)
                                Console.WriteLine($"        - {note}");
                        }
                    }
                    var passedCount = validated.Count(v => v.Outcome.Passed);
                    Console.WriteLine($"[{source.SourceName}] {targetDate:yyyy-MM-dd} 場={trackCode} Validator結果: " +
                        $"公開可={passedCount}件 公開停止={validated.Count - passedCount}件");

                    var top5 = scoresStore.GetVenueTop5(targetDate, trackCode);
                    var trendMorning = trendStore.Get(targetDate, trackCode, TrendStage.Morning);
                    var trendLive = trendStore.Get(targetDate, trackCode, TrendStage.Live);
                    var trendFinal = trendStore.Get(targetDate, trackCode, TrendStage.Final);

                    // WordPress送信の失敗は仕様書§15「WordPress API失敗→再試行＋通知」・
                    // §21受け入れ基準「WordPress更新失敗時に通知される」の対象。
                    // WordPressClient側で最大4回再試行済みのため、ここに例外が来た時点で
                    // 再試行が尽きた後の最終失敗を意味する。他の失敗（生成・検証）と区別して
                    // critical=trueで通知し、かつ1開催場の公開失敗で他の開催場を巻き添えにしない
                    // よう、この呼び出しだけ個別にtry/catchする。
                    try
                    {
                        var publishOutcome = publisher
                            .PublishAsync(targetDate, trackCode, isCentral, top5, trendMorning, trendLive, trendFinal, validated)
                            .GetAwaiter().GetResult();

                        Console.WriteLine(publishOutcome.WasPublished
                            ? $"[{source.SourceName}] {targetDate:yyyy-MM-dd} 場={trackCode} WordPress公開完了（ピック{publishOutcome.PublishedPickCount}件、公開停止{publishOutcome.BlockedPickCount}件）"
                            : $"[{source.SourceName}] {targetDate:yyyy-MM-dd} 場={trackCode} WordPress公開を見送り: {publishOutcome.SkipReason}");
                    }
                    catch (Exception publishEx)
                    {
                        LogFailure(source.SourceName, $"{trackCode} のWordPress公開に失敗（再試行済み。このレース場のみスキップして続行）", publishEx, critical: true);
                    }
                }
                LogSuccess(source.SourceName, "コンテンツ生成・公開", "正常終了");
            }
            catch (Exception ex)
            {
                LogFailure(source.SourceName, "本日の狙い馬・穴馬・危険な人気馬の生成・公開に失敗（このソースのみスキップして続行）", ex);
            }
        }

        private static void RunVerifyFor(JvSpecComDataSource source, PredictionStore predictionStore, VerificationStore verificationStore, DateTime targetDate)
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

                var verifier = new VerificationService(source, predictionStore, verificationStore);
                foreach (var trackCode in venues)
                {
                    var summary = verifier.VerifyVenue(targetDate, trackCode);
                    Console.WriteLine($"[{source.SourceName}] {targetDate:yyyy-MM-dd} 場={trackCode} " +
                        $"検証: 対象{summary.TotalPredictions}件 新規検証{summary.Verified}件 未確定{summary.StillPending}件");
                }
                LogSuccess(source.SourceName, "レース後検証", "正常終了");
            }
            catch (Exception ex)
            {
                LogFailure(source.SourceName, "レース後検証に失敗（このソースのみスキップして続行）", ex);
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
                    // ここまでの再試行がすべて尽きた（＝これ以上自動では回復しない）ことを意味するため、
                    // 仕様書§15「WordPress API失敗→再試行＋通知」・§21「WordPress更新失敗時に通知される」
                    // に対応してcritical通知にする。残りのレースの結果・払戻がその日反映されないまま
                    // 終わる可能性がある、運用者が気付くべき状態のため。
                    LogFailure(source.SourceName,
                        $"監視が{attempt}回の再試行後も本日の打ち切り時刻までに完了しませんでした（残りのレースが未反映の可能性）",
                        new InvalidOperationException("watch loop exhausted retries before daily cutoff"),
                        critical: true);
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

        // ---- stats（仕様書§18 指数帯別成績） ----

        private static void RunStatsCommand(VerificationStore store, string[] args)
        {
            // stats <category> <modelVersion>
            // category: Nerai / Ana / Kiken / AiIndexTop5
            if (args.Length < 2)
            {
                Console.WriteLine("使い方: KeibaDataCollector.exe stats <Nerai|Ana|Kiken|AiIndexTop5> <modelVersion>");
                Console.WriteLine($"  modelVersionの既定値（AI Scoring Engineの現行バージョン）: {AiIndexService.ModelVersion}");
                return;
            }

            var category = args[0];
            var modelVersion = args[1];
            var bands = store.GetIndexBandStats(modelVersion, category);

            if (bands.Count == 0)
            {
                Console.WriteLine($"[stats] category={category} model_version={modelVersion} の検証データがまだありません。");
                return;
            }

            Console.WriteLine($"[stats] category={category} model_version={modelVersion}");
            Console.WriteLine("  指数帯      母数   3着内率   勝率");
            foreach (var b in bands)
            {
                Console.WriteLine($"  {b.BandLow,3}-{b.BandHigh,-3}    {b.SampleCount,4}   {b.Top3Rate,7:P1}  {b.WinRate,7:P1}");
            }
        }

        // ---- dashboard（仕様書§17 監視ダッシュボード） ----

        private static void PrintDashboard(MonitoringSnapshot s)
        {
            Console.WriteLine($"=== 監視ダッシュボード {s.RaceDate:yyyy-MM-dd}（生成: {s.GeneratedAtUtc:u}） ===");

            Console.WriteLine($"当日開催場（データあり）: {(s.VenuesWithData.Count > 0 ? string.Join(", ", s.VenuesWithData) : "なし")}");
            Console.WriteLine($"最終データ同期時刻: {Fmt(s.LastDataSyncUtc)}");
            Console.WriteLine($"最終AI計算時刻: {Fmt(s.LastAiComputeUtc)}");
            Console.WriteLine($"最終WordPress更新時刻: {Fmt(s.LastWordPressPublishUtc)}");

            Console.WriteLine("データソース接続状態:");
            foreach (var kv in s.DataSourceStatus)
                Console.WriteLine($"  {kv.Key}: {kv.Value}");

            Console.WriteLine($"LicenseGate: JRA={(s.JraLicenseVisible ? "公開可" : "公開停止")}");
            foreach (var (venueId, visible) in s.LocalVenueLicenseVisible)
                Console.WriteLine($"  地方 {venueId}: {(visible ? "公開可" : "公開停止")}");

            Console.WriteLine($"未処理レース数（score済みだがcontent未実行）: {s.UnprocessedRaceCount}");

            Console.WriteLine("エラー件数（直近24時間）:");
            if (s.ErrorCountLast24h.Count == 0)
                Console.WriteLine("  なし");
            foreach (var kv in s.ErrorCountLast24h)
                Console.WriteLine($"  {kv.Key}: {kv.Value}件");

            Console.WriteLine($"公開停止理由（本日、重複除去）:");
            if (s.PublishBlockedReasons.Count == 0)
                Console.WriteLine("  なし");
            foreach (var reason in s.PublishBlockedReasons)
                Console.WriteLine($"  - {reason}");

            Console.WriteLine($"自動公開: {(s.AutoPublishEnabled ? "ON" : "OFF")}");
            Console.WriteLine("手動再実行: run-score.bat / run-content.bat 等をVPS上で対象日指定で実行してください。");
        }

        private static string Fmt(DateTime? d) => d.HasValue ? d.Value.ToString("u") : "記録なし";
    }
}
