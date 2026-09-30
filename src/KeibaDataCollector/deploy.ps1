<#
.SYNOPSIS
    最新版を取得してビルドし、タスクを登録し直して再開する。更新時はこれだけ実行する。

.DESCRIPTION
    「停止 → 取得 → ビルド → 登録 → 再開」を必ずこの順で行う。
    手作業で組み立てると、これまで実際に次の失敗を繰り返した。

      - 稼働中に dotnet build を実行 → exe がロックされていて
        「別のプロセスが使用中のため…にコピーできませんでした」でビルド失敗。
        しかも失敗に気づかず再開すると、修正前のexeがそのまま動き続ける。
      - Stop-ScheduledTask はバッチを止めるだけで、そこから起動された
        KeibaDataCollector.exe が残ることがある。
      - タスク定義を置き換えると実行中のインスタンスが終了する。watchは
        トリガーが1日1回なので、日中にやるとその日はもう動かない
        （2026-08-11に発生。結果の反映が57レース中18レースで止まった）。

    ビルドに失敗した場合はタスクを再開せずに中断する。
    修正前のexeで動き続けるほうが、止まっているより気づきにくく害が大きい。

    重要: 同じVPS上に稼働している既存2システム（`keiba-race-result-auto-posting` /
    `horse-race-custom-builder`）も同名の実行ファイル `KeibaDataCollector.exe` を使っている。
    プロセスを名前だけで止めると既存システムまで巻き込んで終了させてしまうため、
    このスクリプトは実行パス（$scriptDir配下）で絞り込んでから停止する。
    タスク名も既定で"KeibaAiDataSystem-*"（-TaskPrefixで変更可）とし、既存システムの
    "KeibaDataCollector-*"とは別名前空間にしている。

.PARAMETER SkipPull
    git pull を行わず、現在のソースのままビルドし直す場合に指定する。

.PARAMETER SkipRegister
    タスクの登録し直しを省略する。スケジュール（時刻・間隔）を変更していない
    ときに指定すると少し速い。

.PARAMETER TaskPrefix
    タスク名のプレフィックス。register-scheduled-tasks.ps1と同じ既定値
    "KeibaAiDataSystem"を使う（変更する場合は両方に同じ値を渡すこと）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\deploy.ps1
#>
[CmdletBinding()]
param(
    [switch] $SkipPull,
    [switch] $SkipRegister,
    [string] $TaskPrefix = 'KeibaAiDataSystem'
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# 常駐・繰り返し実行されるタスク。停止したら最後に必ず戻す。
$RunningTasks = @("$TaskPrefix-Watch", "$TaskPrefix-Predict")
$AllTasks     = @("$TaskPrefix-Morning") + $RunningTasks

function Write-Step([string] $message) {
    Write-Output ""
    Write-Output "==== $message ===="
}

# --- 1. 止める ---------------------------------------------------------------
Write-Step '実行中のタスクとプロセスを停止'
foreach ($task in $AllTasks) {
    if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
        Write-Output "  停止要求: $task"
    }
}

# タスクを止めても子プロセスが残る。残っているとexeを上書きできない。
# 同名の実行ファイルが既存2システムでも動いているため、名前だけでなく実行パス
# （このディレクトリ配下）でも絞り込み、他システムのプロセスを誤って止めないようにする。
function Get-OwnKeibaDataCollectorProcesses {
    Get-Process KeibaDataCollector -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($scriptDir, [StringComparison]::OrdinalIgnoreCase) }
        catch { $false }  # 権限不足等でPathが取れない場合は誤って対象に含めない
    }
}

$procs = Get-OwnKeibaDataCollectorProcesses
if ($procs) {
    Write-Output ("  残存プロセスを終了: PID {0}" -f (($procs | ForEach-Object { $_.Id }) -join ', '))
    $procs | Stop-Process -Force
    for ($i = 0; $i -lt 40; $i++) {          # ロックが解けるまで最大10秒待つ
        Start-Sleep -Milliseconds 250
        if (-not (Get-OwnKeibaDataCollectorProcesses)) { break }
    }
}
if (Get-OwnKeibaDataCollectorProcesses) {
    throw "プロセスが終了しません。ビルドすると失敗するため中断します。VPSの画面にダイアログが出ていないか確認してください。"
}
Write-Output "  実行中のプロセスはありません（他システムのKeibaDataCollector.exeは対象外）"

# --- 2. 最新版を取得 ---------------------------------------------------------
if (-not $SkipPull) {
    Write-Step '最新版を取得 (git pull)'
    git pull origin main
    if ($LASTEXITCODE -ne 0) {
        throw "git pull に失敗しました。タスクは再開していません。"
    }
}

# --- 3. ビルド ---------------------------------------------------------------
Write-Step 'ビルド'
dotnet build -c Debug
if ($LASTEXITCODE -ne 0) {
    throw "ビルドに失敗しました。タスクは再開していません。上のエラーを確認してください。"
}

# --- 4. タスクを登録し直す ---------------------------------------------------
# 実行時刻や繰り返し間隔を変更した場合、ここを通さないと反映されない。
if (-not $SkipRegister) {
    Write-Step 'タスクを登録し直す'
    & powershell -ExecutionPolicy Bypass -File (Join-Path $scriptDir 'register-scheduled-tasks.ps1') -TaskPrefix $TaskPrefix
    if ($LASTEXITCODE -ne 0) {
        throw "タスクの登録に失敗しました。タスクは再開していません。"
    }
}

# --- 5. 再開 -----------------------------------------------------------------
Write-Step 'タスクを再開'
foreach ($task in $RunningTasks) {
    Start-ScheduledTask -TaskName $task
    Write-Output "  開始: $task"
}
Start-Sleep -Seconds 3

Write-Step '状態'
Get-ScheduledTask -TaskName "$TaskPrefix-*" | Format-Table TaskName, State -AutoSize

$today = Get-Date -Format 'yyyyMMdd'
Write-Step '数分後に確認してください'
Write-Output "  Get-Content '.\logs\predict-$today.log' -Encoding UTF8 | Select-String '反映完了'"
Write-Output "  Get-Content '.\logs\watch-$today.log'   -Encoding UTF8 -Tail 20"
Write-Output ""
Write-Output "確認ポイント:"
Write-Output "  ・予想: 「（所要 ◯分）」が繰り返し間隔(15分)を超えていないこと"
Write-Output "  ・予想: 「オッズ取得エラー」が0件であること"
Write-Output "  ・監視: 「監視中... メモリ◯MB」が横ばいであること"
