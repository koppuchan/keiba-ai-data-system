<#
.SYNOPSIS
    承認済みの公開許諾（JRA・地方競馬の各開催場）をLicenseGateへ反映する。

.DESCRIPTION
    LicenseGateの状態はSQLiteに永続化されるため、このスクリプトは初回と、承認範囲が
    変わったとき（開催場の追加・許諾の取り消し等）にだけ実行する。deploy.ps1には
    組み込まない: デプロイのたびに「承認済み」へ上書きされると、後から取り消した許諾が
    次回のデプロイで黙って復活してしまい、LicenseGateの意味が無くなるため。

    承認範囲が変わったら、下の $JraApproved / $LocalVenues を編集して再実行する。
    取り消す場合は該当の開催場を一覧から外すのではなく、-Revoke で明示的に
    rejectedへ変更すること（一覧から外しただけでは既存の行が承認済みのまま残る）。

.PARAMETER Note
    監査用の備考。許諾の根拠（確認日・連絡手段等）を残す。

.PARAMETER Revoke
    指定した開催場ID（地方）をrejectedに変更する。JRAを取り消す場合は "jra" を指定。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\apply-licensegate.ps1 -Note "2026-10-01 クライアントより許諾承認確認"

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\apply-licensegate.ps1 -Revoke 55 -Note "2026-11-01 佐賀の契約終了"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Note,
    [string[]] $Revoke = @()
)

$ErrorActionPreference = 'Stop'

# 中央競馬（JRA）: Web公開許諾・商用利用許諾の両方が承認されている。
$JraApproved = $true

# 地方競馬: 開催場ID => 表示名（表示用のラベルで、判定にはIDだけが使われる）。
$LocalVenues = [ordered]@{
    '30' = '門別'
    '43' = '船橋'
    '48' = '名古屋'
    '50' = '園田'
    '55' = '佐賀'
}

$exe = Join-Path $PSScriptRoot 'bin\Debug\net48\KeibaDataCollector.exe'
if (-not (Test-Path $exe)) {
    throw "KeibaDataCollector.exe が見つかりません: $exe`nビルド済みか確認してください（dotnet build -c Debug）。"
}

function Invoke-LicenseGate {
    & $exe licensegate @args | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "licensegate $($args -join ' ') が失敗しました（終了コード $LASTEXITCODE）。"
    }
}

if ($Revoke -contains 'jra') {
    Invoke-LicenseGate set-jra active rejected rejected $Note
}
elseif ($JraApproved) {
    Invoke-LicenseGate set-jra active approved approved $Note
}

foreach ($id in $LocalVenues.Keys) {
    $approval = if ($Revoke -contains $id) { 'rejected' } else { 'approved' }
    Invoke-LicenseGate set-local $id $LocalVenues[$id] active $approval $Note
}

foreach ($id in $Revoke) {
    if ($id -ne 'jra' -and -not $LocalVenues.Contains($id)) {
        Invoke-LicenseGate set-local $id $id inactive rejected $Note
    }
}

& $exe licensegate show
