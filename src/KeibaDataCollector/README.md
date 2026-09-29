# KeibaDataCollector

`keiba-race-result-auto-posting` / `horse-race-custom-builder`（既存稼働中システム）を土台に、
JRAVAN＋競馬最強の法則WEB 全自動AI競馬データシステム仕様書の各コンポーネントを追加していくプロジェクト。
詳細は [`DEVELOPMENT_PLAN.md`](../../DEVELOPMENT_PLAN.md) を参照。

## 現状（Issue #1: LicenseGate 土台）

仕様書§5 LicenseGateのみ実装済み。「データを取得できる」ことと「そのデータをWebで公開できる」ことを
明確に分離するための永続化層（`Data/LicenseGateStore.cs`）と、それを操作する暫定CLIコマンド。

- JRA-VAN（中央競馬）は全国一律の契約のため `jra_license` テーブルに1行のみ
- 競馬最強の法則WEB / 地方競馬DATA（UmaConn）は競馬場ごとに契約が分かれるため
  `local_venue_license` に venue_id ごとの行
- **未設定・行が存在しない場合は常に非公開（フェイルクローズ）**。取得可否はこの判定に一切関与しない

Source Adapter（JV-Link/UmaConn連携）・WordPress Publisherへの実接続はまだ無い
（それぞれ Issue #2 / Issue #6, #7 で実装）。現時点ではCLIから状態を確認・更新できるだけ。

```
KeibaDataCollector.exe licensegate show
KeibaDataCollector.exe licensegate set-jra active approved approved "2026-xx-xx JRA-VANより書面確認"
KeibaDataCollector.exe licensegate set-local 35 盛岡 active approved "2026-xx-xx 契約書PDF確認済み"
KeibaDataCollector.exe licensegate check 35 local
```

## ビルドについて

JV-Link/UmaConn連携を前提に `PlatformTarget=x86` / `net48` で構成している（Issue #2以降で
COM相互運用コードが合流する）。**.NET Framework 4.8のビルド環境はWindowsが前提**で、この
リポジトリの開発は元々Linux上のエージェントから行っているためローカルでは `dotnet build` を
検証できていない。マージ前に一度Windows機（またはWindows上のCI）でビルドを確認すること。

## 機密情報の扱い

利用キー・WordPress認証情報等は環境変数で注入する方針（`AppConfig.cs`参照）。
`App.config` には非機密の既定値のみをコミットする。LicenseGateの承認状態そのものは機密ではないが、
保存先の `historical.sqlite3` はgit管理対象外（`.gitignore`参照）。
