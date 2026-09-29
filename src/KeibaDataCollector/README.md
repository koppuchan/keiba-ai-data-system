# KeibaDataCollector

`keiba-race-result-auto-posting` / `horse-race-custom-builder`（既存稼働中システム、同じ開発者による
同一構成のWindows常駐アプリ）を土台に、JRAVAN＋競馬最強の法則WEB 全自動AI競馬データシステム仕様書の
各コンポーネントを追加していくプロジェクト。詳細は [`DEVELOPMENT_PLAN.md`](../../DEVELOPMENT_PLAN.md) を参照。

## 現状（Issue #3まで: 既存機能のポート + LicenseGate土台 + AI指数エンジン）

`horse-race-custom-builder` の実装をそのまま移植し、このリポジトリ単体で既存システムと同等の
コマンド一式（`setup` / `morning` / `predict` / `score` / `watch` / `probe` / `backfill` / `dbstats`）
が揃っている状態。挙動は移植元と同じで、仕様書独自の新機能（Trend Engine・Content Generator・
Validator等）はまだ無い。移植元との差分は `licensegate` コマンドの追加のみ。

```
KeibaDataCollector.exe setup       # 初回のみ。利用キー等をGUIダイアログで登録
KeibaDataCollector.exe morning     # 朝一: 当日の出走表取得→WordPress反映
KeibaDataCollector.exe watch       # レース確定監視→結果・払戻を随時反映
KeibaDataCollector.exe predict     # 朝一オッズの人気順から予想印を生成→反映
KeibaDataCollector.exe score       # 6ファクター算出→WordPress(hrc_factors)へ反映
KeibaDataCollector.exe backfill    # 6ファクター用の過去データ取得
KeibaDataCollector.exe probe       # 調査用: どのデータ種別で何が取れるか確認（WordPressへ書き込まない）
KeibaDataCollector.exe dbstats     # 蓄積済みSQLiteの件数・日付範囲を確認
KeibaDataCollector.exe licensegate # LicenseGateの確認・更新（下記）
```

運用（Windows Task Scheduler登録、`deploy.ps1`によるビルド→再起動手順等）も既存2リポジトリと
同じ運用スクリプトをそのまま同梱している。詳細な注意点（JV-Link Setup取得は無人実行不可、
ダイアログ対策、ページキャッシュ、地方競馬のデータ欠損傾向等）は移植元リポジトリのREADMEに
記載されている運用知見がそのまま当てはまる。

## LicenseGate（仕様書§5）

「データを取得できる」ことと「そのデータをWebで公開できる」ことを明確に分離するための永続化層
（`Data/LicenseGateStore.cs`）。

- JRA-VAN（中央競馬）は全国一律の契約のため `jra_license` テーブルに1行のみ
- 競馬最強の法則WEB / 地方競馬DATA（UmaConn）は競馬場ごとに契約が分かれるため
  `local_venue_license` に venue_id ごとの行
- **未設定・行が存在しない場合は常に非公開（フェイルクローズ）**。取得可否はこの判定に一切関与しない

WordPress Publisherへの実接続はまだ無い（Issue #6 Validator / Issue #7 Publisherで接続）。
現時点ではCLIから状態を確認・更新できるだけ。

```
KeibaDataCollector.exe licensegate show
KeibaDataCollector.exe licensegate set-jra active approved approved "2026-xx-xx JRA-VANより書面確認"
KeibaDataCollector.exe licensegate set-local 35 盛岡 active approved "2026-xx-xx 契約書PDF確認済み"
KeibaDataCollector.exe licensegate check 35 local
```

## AI指数エンジン（仕様書§8・§9）

`score`コマンド実行時に、既存の6ファクター算出（`FactorScoringService`、変更なし）に加えて
`Services/AiIndexService.cs`が単一の「AI指数」へ加重平均で統合し、`scores`テーブルへ保存する。

- 加重平均は **Σ(値×重み) / Σ(重み)**（算出できた＝null出ないファクターのみ対象）。単純合計では
  ないので、算出できたファクター数が多い馬が自動的に有利になることはない
  （horse-race-custom-builderのフロントエンドで実際に発生した不具合の教訓。同READMEを参照）
- 重みは`score_weights`テーブルのDB設定値。セグメント（`central`/`local` × `turf`/`dirt`、
  例: `central:turf`）ごとに個別設定でき、未設定なら`default`→全項目1.0の順にフォールバックする
- `model_version`（採点式のバージョン）・`feature_version`（特徴量抽出ロジックのバージョン）・
  `data_cutoff_utc`（その算出が見たデータの基準時刻）を毎回の算出結果に必ず付与する
- 取消・除外馬（SEレコードの異常区分コード≠0）はAI指数TOP5から自動的に除外される
- AI指数TOP5は開催場（開催日×競馬場）全体で上位5頭。同点はデータ充足率→馬番の順でタイブレーク

```
KeibaDataCollector.exe weights show central:turf
KeibaDataCollector.exe weights set central:turf 1.2 1.0 1.0 1.5 0.8 1.0
```

WordPressへのAI指数TOP5の実publishはまだ無い（算出結果はscoreコマンド実行時にコンソールへログ出力
されるのみ）。Content Generator（Issue #5）・WordPress Publisher拡張（Issue #7）で接続する。

## ビルドについて

JV-Link/UmaConn連携を前提に `PlatformTarget=x86` / `net48` で構成している（Issue #2以降で
COM相互運用コードが合流する）。**.NET Framework 4.8のビルド環境はWindowsが前提**で、この
リポジトリの開発は元々Linux上のエージェントから行っているためローカルでは `dotnet build` を
検証できていない。マージ前に一度Windows機（またはWindows上のCI）でビルドを確認すること。

## 機密情報の扱い

利用キー・WordPress認証情報等は環境変数で注入する方針（`AppConfig.cs`参照）。
`App.config` には非機密の既定値のみをコミットする。LicenseGateの承認状態そのものは機密ではないが、
保存先の `historical.sqlite3` はgit管理対象外（`.gitignore`参照）。
