<#
.SYNOPSIS
    KeibaDataCollector の朝一バッチ／確定監視をタスクスケジューラへ登録する。

.DESCRIPTION
    仕様書§12「自動更新スケジュール」に対応するタスクを作成します。タスク名は既定で
    "KeibaAiDataSystem-*"（-TaskPrefixで変更可）。

      KeibaAiDataSystem-BackfillIncremental   : 毎日 -BackfillTime             に scheduled-backfill.bat incremental（深夜）
      KeibaAiDataSystem-Score                 : 毎日 -ScoreTime               から繰り返し scheduled-score.bat（朝〜発走前〜レース間）
      KeibaAiDataSystem-TrendMorning          : 毎日 -TrendMorningTime        に scheduled-trend.bat morning（朝）
      KeibaAiDataSystem-TrendLive             : 毎日 -TrendLiveTime           から繰り返し scheduled-trend.bat live（開催中）
      KeibaAiDataSystem-Content                : 毎日 -ContentTime            から繰り返し scheduled-content.bat（発走前〜レース間）
      KeibaAiDataSystem-TrendFinal            : 毎日 -TrendFinalTime          に scheduled-trend.bat final（開催終了後）
      KeibaAiDataSystem-Verify                : 毎日 -VerifyTime              に scheduled-verify.bat（開催終了後）
      KeibaAiDataSystem-Backup                : 毎日 -BackupTime              に scheduled-backup.bat（深夜、DBバックアップ）
      KeibaAiDataSystem-Dashboard             : 毎日 -DashboardTime           から繰り返し scheduled-dashboard.bat（日中）

    意図的にmorning/predict/watchは登録しません。既存システム（`keiba-race-result-auto-posting` /
    `horse-race-custom-builder`）が同じWordPress投稿（`race`カスタム投稿タイプ）へ既に出走表・
    予想印・結果を反映しており、仕様書§2が求めているのは既存の予想ページへの「連携」であって
    二重公開ではないため。scoreコマンドもWordPressのhrc_factorsへは送らずscoresテーブルへの
    保存のみ行う（同じ理由）。既存システムと同居させない・将来的に統合するなどの理由でこれらも
    登録したい場合は、KeibaDataCollector.exe morning/predict/watchを個別にRegister-ScheduledTask
    すること（本スクリプトはあえて対応していない）。

    watch モードは当日の全レースが確定すると自身で終了するため、停止トリガーは不要です。

    重要: 同じVPS上に `keiba-race-result-auto-posting` / `horse-race-custom-builder`
    （同じ開発者による同一構成のアプリ。実行ファイル名・タスク名の既定が同じ"KeibaDataCollector-*"）
    が既に稼働している場合、タスク名が衝突する。このスクリプトは既存タスクと同名のタスクを
    見つけると「更新」として上書き登録する仕様のため、もし両システムが同じタスク名で動いていると
    後から登録した側が先方のタスク定義（実行するbatのパス）を書き換えてしまい、既存システムが
    静かに壊れる。そのため既定のプレフィックスを"KeibaAiDataSystem"とし、既存2システムの
    "KeibaDataCollector-*"とは別名前空間にしている。

    「前日夜: 翌日開催場・出走予定を準備」（仕様書§12）に対応する専用タスクは意図的に作っていない。
    出馬表は開催日より前に配信されるため（JV-Data仕様書）、`morning`は当日分を早朝に取得すれば
    間に合う設計のまま（既存のRaceDiscovery/RaceCardServiceが対象日を"ThisWeekAndToday"で
    取得済み）。前日時点で「翌日」を明示的に対象にするモードはコマンド側に無く、それを追加するには
    C#側の変更が必要なため本スクリプトの範囲外とした。

    重要な前提:
      JV-Link / UmaConn の利用キーは「setup を実行したWindowsユーザー」の
      レジストリに保存されます。そのため、このタスクは必ず同じユーザーで
      実行される必要があります。既定では、このスクリプトを実行している
      ユーザー自身が登録されます。

.PARAMETER RunOnlyWhenLoggedOn
    指定すると「ログオン時のみ実行」で登録します（既定）。
    JV-Link / UmaConn はダイアログを出すことがあり、非対話セッションだと
    それが見えないまま処理が止まる可能性があるため、まずはこちらを推奨します。
    -RunOnlyWhenLoggedOn:$false を指定すると、パスワードを保存して
    「ログオンしていなくても実行する」で登録します（要パスワード入力）。

.PARAMETER TaskPrefix
    タスク名のプレフィックス。既定は "KeibaAiDataSystem"。同じVPS上の既存2システム
    （既定のタスク名が"KeibaDataCollector-*"）と名前空間が衝突しないようにするため、
    変更する場合も既存タスク名と重複しない値にすること。

.EXAMPLE
    # 既定（ログオン時のみ実行）
    powershell -ExecutionPolicy Bypass -File .\register-scheduled-tasks.ps1

.EXAMPLE
    # 時刻を変える
    powershell -ExecutionPolicy Bypass -File .\register-scheduled-tasks.ps1 -ScoreTime 07:15 -VerifyTime 22:30
#>
[CmdletBinding()]
param(
    [string] $TaskPrefix = 'KeibaAiDataSystem',
    # 以下、仕様書§12対応のタスクの時刻。深夜→朝→発走前/レース間→開催終了後、の順。
    [string] $BackfillTime = '02:00',
    [string] $ScoreTime = '07:30',
    [string] $TrendMorningTime = '07:45',
    [string] $TrendLiveTime = '09:00',
    [string] $ContentTime = '07:40',
    [string] $TrendFinalTime = '21:30',
    [string] $VerifyTime = '22:00',
    [string] $DashboardTime = '07:00',
    [string] $BackupTime = '01:30',
    [switch] $RunOnlyWhenLoggedOn = $true
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
# 登録し直したことで停止したタスクを、あとでまとめて再開するために覚えておく。
$script:TasksToResume = @()

$backfillBat = Join-Path $scriptDir 'scheduled-backfill.bat'
$scoreBat = Join-Path $scriptDir 'scheduled-score.bat'
$trendBat = Join-Path $scriptDir 'scheduled-trend.bat'
$contentBat = Join-Path $scriptDir 'scheduled-content.bat'
$verifyBat = Join-Path $scriptDir 'scheduled-verify.bat'
$dashboardBat = Join-Path $scriptDir 'scheduled-dashboard.bat'
$backupBat = Join-Path $scriptDir 'scheduled-backup.bat'
$exePath = Join-Path $scriptDir 'bin\Debug\net48\KeibaDataCollector.exe'
$secrets = Join-Path $scriptDir 'secrets.local.bat'

# --- 事前チェック ------------------------------------------------------------
foreach ($required in @($backfillBat, $scoreBat, $trendBat, $contentBat, $verifyBat, $dashboardBat, $backupBat, $exePath)) {
    if (-not (Test-Path $required)) {
        throw "必要なファイルが見つかりません: $required`nビルド済みか確認してください（dotnet build -c Debug）。"
    }
}
if (-not (Test-Path $secrets)) {
    throw "secrets.local.bat が見つかりません: $secrets`nsecrets.local.bat.example をコピーして値を設定してください。"
}

$currentUser = "$env:USERDOMAIN\$env:USERNAME"
Write-Output "登録ユーザー : $currentUser"
Write-Output "作業ディレクトリ: $scriptDir"
Write-Output ""

# --- 登録 --------------------------------------------------------------------
function Register-KeibaTask {
    param(
        [string] $TaskName,
        [string] $BatPath,
        [string] $StartTime,
        [string] $Description,
        # scheduled-trend.batのように %1 で段階（morning/live/final）を要求するbatに渡す引数。
        # 空文字なら引数無しで呼ぶ（他のbatは%1省略時=当日として動くため）。
        [string] $Argument = '',
        # 指定すると、開始時刻から $RepeatFor の間、$RepeatEvery ごとに繰り返し実行する。
        [timespan] $RepeatEvery,
        [timespan] $RepeatFor
    )

    if ($Argument) {
        $action = New-ScheduledTaskAction -Execute $BatPath -Argument $Argument -WorkingDirectory $scriptDir
    }
    else {
        $action = New-ScheduledTaskAction -Execute $BatPath -WorkingDirectory $scriptDir
    }
    $trigger = New-ScheduledTaskTrigger -Daily -At $StartTime

    # 速報オッズは「対象レースの勝ち馬投票券発売以降」にしか提供されない
    # （JV-Data仕様書「（２）速報系データ」）。発売開始は競馬場ごとに違うため、
    # 1回の実行では全レースを賄えない
    # （実測 2026-08-11: 09:31時点で57レース中2件、10:05時点で24件。
    #   盛岡・笠松は朝に全レース揃う一方、浦和・門別・金沢は12:30時点でも0件）。
    #
    # そのため開催時間全体を覆うように繰り返す。門別のナイターは最終レースが20時台のため、
    # 09:00から12時間（21:00まで）とする。ここを短くすると、
    # 発売が遅い競馬場の後半レースに予想が付かないまま終わる。
    # 既に予想が入っているレースは上書きしないので、何度走らせても最初の値が残る。
    #
    # 間隔は15分。オッズ公開から反映までの遅れが「間隔＋実行時間」になるため、
    # 30分間隔だと最悪50分ほど遅れ、お客様から指摘を受けた（2026-08-12）。
    # 反映済み・確定済みのレースはWordPress照会だけで飛ばすので、
    # 日中に進むほど1回の実行は短くなる。
    if ($RepeatEvery -gt [timespan]::Zero) {
        $trigger.Repetition = (New-ScheduledTaskTrigger -Once -At $StartTime `
            -RepetitionInterval $RepeatEvery -RepetitionDuration $RepeatFor).Repetition
    }

    # 同じCOMオブジェクトを二重に開かないよう多重起動を禁止する。
    # 電源/ネットワーク条件でスキップされないようにもしておく（VPSは常時電源のため）。
    $settings = New-ScheduledTaskSettingsSet `
        -MultipleInstances IgnoreNew `
        -StartWhenAvailable `
        -DontStopIfGoingOnBatteries `
        -AllowStartIfOnBatteries `
        -ExecutionTimeLimit (New-TimeSpan -Hours 20)

    if ($RunOnlyWhenLoggedOn) {
        # 対話セッションで実行。ダイアログが出た場合に気づける。
        $principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Highest
    }
    else {
        # ログオンしていなくても実行。パスワードの保存が必要。
        $principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Password -RunLevel Highest
    }

    $task = New-ScheduledTask -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Description $Description

    # タスク定義を置き換えると、実行中のインスタンスはその場で終了する。
    # watch はトリガーが1日1回しかないため、日中に登録し直すと
    # その日はもう再開せず、以降のレース結果が反映されないまま終わる
    # （実測 2026-08-11: 15:10にこのスクリプトを再実行した結果、
    #   57レース中18レースで結果の反映が止まった）。
    # 実行中だったタスクは登録後に必ず再開する。
    $wasRunning = $false
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        $wasRunning = ((Get-ScheduledTask -TaskName $TaskName).State -eq 'Running')
        Write-Output ("既存タスクを更新します: $TaskName" + $(if ($wasRunning) { "（実行中→登録後に再開します）" } else { "" }))
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }

    if ($wasRunning) { $script:TasksToResume += $TaskName }

    if ($RunOnlyWhenLoggedOn) {
        Register-ScheduledTask -TaskName $TaskName -InputObject $task | Out-Null
    }
    else {
        $cred = Get-Credential -UserName $currentUser -Message "$TaskName を「ログオンしていなくても実行」で登録します。$currentUser のパスワードを入力してください。"
        Register-ScheduledTask -TaskName $TaskName -InputObject $task -User $cred.UserName -Password $cred.GetNetworkCredential().Password | Out-Null
    }

    Write-Output "登録しました: $TaskName ($StartTime 毎日)"
}

Register-KeibaTask -TaskName "$TaskPrefix-BackfillIncremental" -BatPath $backfillBat -StartTime $BackfillTime `
    -Description '深夜: 6ファクター/AI指数用の履歴データを差分取得する（option=Normal、ダイアログ無し）'

Register-KeibaTask -TaskName "$TaskPrefix-Score" -BatPath $scoreBat -StartTime $ScoreTime `
    -Description '朝〜発走前〜レース間: 当日出走馬のAI指数を算出しscoresテーブルへ保存する（WordPressへは送らない。繰り返し。取消・騎手変更・馬場変更の再計算もこの再実行で反映される）' `
    -RepeatEvery (New-TimeSpan -Minutes 20) -RepeatFor (New-TimeSpan -Hours 14)

Register-KeibaTask -TaskName "$TaskPrefix-TrendMorning" -BatPath $trendBat -StartTime $TrendMorningTime `
    -Argument 'morning' `
    -Description '朝: 過去データ+当日確定の天候・馬場状態から事前想定傾向を算出する（1回のみ）'

Register-KeibaTask -TaskName "$TaskPrefix-TrendLive" -BatPath $trendBat -StartTime $TrendLiveTime `
    -Argument 'live' `
    -Description '開催中: ここまでの当日結果から現時点の傾向を算出する（繰り返し）' `
    -RepeatEvery (New-TimeSpan -Minutes 30) -RepeatFor (New-TimeSpan -Hours 12)

Register-KeibaTask -TaskName "$TaskPrefix-Content" -BatPath $contentBat -StartTime $ContentTime `
    -Description '発走前〜レース間: 狙い馬・穴馬・危険な人気馬を生成・検証し、AI指数TOP5・傾向とまとめてWordPressへ公開する（繰り返し。Scoreの後に走るよう開始時刻をずらしてある。TrendFinal(21:30)の結果も公開できるよう23:40まで繰り返す）' `
    -RepeatEvery (New-TimeSpan -Minutes 20) -RepeatFor (New-TimeSpan -Hours 16)

Register-KeibaTask -TaskName "$TaskPrefix-TrendFinal" -BatPath $trendBat -StartTime $TrendFinalTime `
    -Argument 'final' `
    -Description '開催終了後: 全当日結果から本日の結果分析を算出する（1回のみ）'

Register-KeibaTask -TaskName "$TaskPrefix-Verify" -BatPath $verifyBat -StartTime $VerifyTime `
    -Description '開催終了後: predictionsを確定着順と突き合わせ、verificationへ記録する（1回のみ）'

Register-KeibaTask -TaskName "$TaskPrefix-Dashboard" -BatPath $dashboardBat -StartTime $DashboardTime `
    -Description '日中: 監視ダッシュボード（仕様書§17）をWordPressへ送信する（繰り返し）' `
    -RepeatEvery (New-TimeSpan -Minutes 30) -RepeatFor (New-TimeSpan -Hours 15)

Register-KeibaTask -TaskName "$TaskPrefix-Backup" -BatPath $backupBat -StartTime $BackupTime `
    -Description '深夜: historical.sqlite3のバックアップを作成する（直近14世代を保持）'

# 登録し直したことで停止したタスクを再開する。
# ここを忘れると、日中に更新した日はその後のレースが反映されないまま終わる。
if ($script:TasksToResume.Count -gt 0) {
    Write-Output ""
    foreach ($name in $script:TasksToResume) {
        Write-Output "実行中だったため再開します: $name"
        Start-ScheduledTask -TaskName $name
    }
}

Write-Output ""
Write-Output "完了しました。確認方法:"
Write-Output "  Get-ScheduledTask -TaskName '$TaskPrefix-*' | Format-Table TaskName,State"
Write-Output "  Start-ScheduledTask -TaskName '$TaskPrefix-Score'   # 手動で試運転"
Write-Output "  Get-ScheduledTaskInfo -TaskName '$TaskPrefix-Score' # 前回結果を確認"
Write-Output ""
Write-Output "ログは $scriptDir\logs\ に日付ごとに出力されます。"
