# KeibaDataCollector

`keiba-race-result-auto-posting` / `horse-race-custom-builder`（既存稼働中システム、同じ開発者による
同一構成のWindows常駐アプリ）を土台に、JRAVAN＋競馬最強の法則WEB 全自動AI競馬データシステム仕様書の
各コンポーネントを追加していくプロジェクト。詳細は [`DEVELOPMENT_PLAN.md`](../../DEVELOPMENT_PLAN.md) を参照。

## 現状（Issue #8まで: 既存機能のポート + LicenseGate + AI指数エンジン + Trend Engine + Content Generator + Validator + WordPress Publisher + レース後検証）

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

`score`コマンド実行時点ではまだWordPressへ送らず、コンソールへログ出力するのみ。実際の公開は
`content`コマンド実行時に`DigestPublisherService`がまとめて行う（後述のWordPress Publisher拡張参照）。

## Trend Engine（仕様書§10 本日の傾向）

`trend`コマンドで、当日開催中の各競馬場について脚質・枠・馬場・上がり・通過順の5軸を集計し、
`trend_snapshots`テーブルへ保存する。3段階（`morning`/`live`/`final`）はそれぞれ独立して実行する
（自動では遷移しない。タスクスケジューラで朝1回・開催中は30〜60分おき・終了後1回、のように登録する）。

```
KeibaDataCollector.exe trend morning   # 過去データ(開催場全体・全年合算)+当日確定の天候・馬場状態
KeibaDataCollector.exe trend live      # ここまでに確定した当日結果を逐次集計
KeibaDataCollector.exe trend final     # 終了後、全当日結果で集計
```

- 最低サンプル数（脚質・枠・上がり=20、通過順=10。レース単位の指標のため少なめ）未満の集計は
  断定的な値（先行有利/差し有利等）を出さず、サンプル数だけを保持する
- 天候・馬場状態は常に当日のRACEデータから直接読む（開催中に馬場状態が変わることがあるため）
- **既知の制約**: `morning`段階の通過順傾向（最終コーナー先頭馬の勝率）は算出できない。
  `BackfillService`が過去分のコーナー通過順（`race_entries.CornerPassage4`）を意図的に
  保存していないため、母集団が無い。`SampleCount=0`のまま返す（捏造しない）。`live`/`final`は
  当日データを直接読むため、この制約を受けずに算出できる

`trend`コマンド実行時点ではまだWordPressへ送らない。`morning`/`live`/`final`いずれの段階も
`trend_snapshots`に保存されるだけで、`content`コマンド実行時にその時点で保存済みの最新スナップショットを
`DigestPublisherService`がまとめて公開する（後述）。

## Content Generator（仕様書§11 今日の狙い馬・穴馬・危険な人気馬）

`content`コマンドで、`score`コマンドが永続化したAI指数（`scores`テーブル）とレース単位のオッズ・
人気（速報オッズ0B31から取得）を突き合わせ、レースごとに狙い馬・穴馬・危険な人気馬を判定する。

- **狙い馬**: レース内でAI指数最上位、かつデータ充足率50%以上、指数55以上（偏差値50=平均なので
  「平均よりやや上」を基準にしている）
- **穴馬**: 6番人気以下（人気薄）だが、レース内のAI指数順位が上位半分に入る馬。オッズを取得できない
  レースは判定しない（仕様書§11の明記通り）
- **危険な人気馬**: 3番人気以内（人気上位）だが、6ファクターのいずれかが偏差値40未満（平均より
  1標準偏差以上低い）の馬。弱点となった項目名を文章に含める
- 文章はDBの実数値のみを埋め込んだ固定テンプレートで生成する（AIに数値や馬番を自由生成させない、
  仕様書§14）。「絶対」「確実」等の禁止表現が万一テンプレートに混入していないかを生成後に
  機械的にチェックする
- 取消・除外馬は、`score`実行時点のスナップショットではなく**生成時点で当日データを読み直して**
  除外する（仕様書§11「取消・騎手変更・馬場変更が未反映なら公開停止または再計算」の取消・除外分に
  対応）。**騎手変更・馬場変更の検知はここでは未対応**（`scores`テーブルが算出時の騎手コード等を
  保持していないため）。公開直前の最終防衛はValidator（Issue #6）が担当する

```
KeibaDataCollector.exe content
KeibaDataCollector.exe content 2026-08-30
```

## Validator（仕様書§14・§5 LicenseGate本接続）

`content`コマンド実行時、生成された各GeneratedPickは公開前に`ValidatorService`を必ず通る。

- **DB再照合**: 生成時点で参照していたAI指数・血統登録番号を、今の`scores`テーブルの値と
  突き合わせる。対象馬が取消・除外になっていたり、血統登録番号が一致しない（馬番の入れ替わり等）、
  AI指数が許容誤差（±2.0）を超えて変化していれば不合格にする。**馬名での照合は未対応**
  （表示名を持つマスタテーブルがまだ無いため、より強い一意キーであるketto_numで代用している）
- **LicenseGate本接続**: `LicenseGateStore.IsWebPublishAllowed(venueId, isCentral)` を呼び、
  この開催場が今Web公開してよい状態かを判定する。LicenseGate導入（Issue #1）以来、
  ここが初めての実接続先になる
- 合否にかかわらず、判定結果は`predictions`テーブルへ**immutableに**保存する（不合格分も残すのは、
  「なぜ公開されなかったか」を後から追跡できるようにするため。仕様書§17監視ダッシュボードの
  「公開停止理由」はここが情報源になる想定）。既存行を書き換えるAPIは`PredictionStore`に
  意図的に用意していない

## WordPress Publisher拡張（仕様書§13、および§3のAI指数TOP5・本日の傾向の公開）

`content`コマンドは、狙い馬・穴馬・危険な人気馬の生成・検証まで終えると、続けてその開催場のAI指数
TOP5（`ScoresStore.GetVenueTop5`）・本日の傾向（`TrendStore`の朝/開催中/終了後3段階）・Validator
通過済みピックを1つにまとめ、WordPressの新規カスタム投稿タイプ`keiba_digest`（`race`投稿とは別、
1開催場・1日単位）へ`DigestPublisherService`経由でidempotentに公開する。

公開直前のゲートは`DigestPublisherService`に集約している。
1. **LicenseGate**: `IsWebPublishAllowed`が通らない開催場は送信自体を行わない
2. **自動公開ON/OFF**: WordPress管理画面（設定 → Keiba AI Digest）のチェックボックスと連動。
   `WordPressClient.IsAutoPublishEnabledAsync`が毎回確認し、取得失敗時も安全側でOFF扱いにする
3. **Validator未通過ピックの除外**: `content`コマンド内で既にIssue #6のValidatorを通している。
   不合格だったピックはpublish対象に含めない
4. **空コンテンツなら送信しない**: AI指数TOP5・傾向・ピックが全て空ならWordPressへ何も送らない
   （仕様書§13「更新失敗時に空ページ・壊れたページを出さない」と同じ考え方をpublish要否にも適用）

WordPress側の対応プラグインは新規
[`wordpress-plugin/keiba-ai-digest/`](../wordpress-plugin/keiba-ai-digest/)。**自動公開の既定値はOFF**
（LicenseGateと同じフェイルクローズ方針）。§13の「手動再実行」は、VPS上で`run-score.bat`/
`run-content.bat`を対象日指定で再実行する運用のままにしている（WordPress側からVPSの処理を
起動する経路は、攻撃面を増やさないため意図的に作っていない）。

## レース後の自動検証（仕様書§18 Verification DB）

`verify`コマンドで、`predictions`テーブル（狙い馬/穴馬/危険な人気馬はIssue #6のValidator、
AI指数TOP5はIssue #7のDigestPublisherが、それぞれ公開のタイミングでimmutableにsnapshot済み）を
当日の確定着順と突き合わせ、`verification`テーブルへ記録する。

- 確定着順は当日のSEレコードを直接読んで得る（historical.sqlite3への当日結果の反映はリアルタイムでは
  ないため、TrendEngine/ContentGeneratorと同じ理由で当日データを直接読む）
- **Validator不合格（非公開）だった予測は検証対象にしない**。公開していない予測の的中率を集計しても
  意味が無いため
- Validator合格分は**的中・不的中を問わずすべて検証対象にする**（仕様書§18「不的中データも削除せず
  全件検証に含める」）。同じ馬が当日中に複数回ピックされた場合はその回数分だけ別々に検証される
  （predictionsが再生成のたびに新しい行を追加する設計のため。既存行を書き換えたり間引いたりしない）
- `verification`テーブルへの書き込みはUpsertだが、書き換えているのは「まだ未確定だった行を確定させる」
  場合のみで、確定済みの結果を後から変えることはない（`verify`を複数回実行しても同じ入力なら同じ
  出力に収束する冪等な操作という位置づけ）

`stats`コマンドで、AI指数を10点刻みの帯に分けた3着内率・勝率を確認できる（仕様書§18「指数帯別成績を
model_versionごとに分離」）。

```
KeibaDataCollector.exe verify
KeibaDataCollector.exe verify 2026-08-30
KeibaDataCollector.exe stats Nerai ai-index-v1
KeibaDataCollector.exe stats AiIndexTop5 ai-index-v1
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
