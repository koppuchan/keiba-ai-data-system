# KeibaDataCollector

`keiba-race-result-auto-posting` / `horse-race-custom-builder`（既存稼働中システム、同一開発者による
同一構成のWindows常駐アプリ）を土台にした、JRAVAN＋競馬最強の法則WEB 全自動AI競馬データシステム。

仕様書の全コンポーネント（Source Adapter〜Verification DB、LicenseGate、監視ダッシュボード）を実装済み。

```
KeibaDataCollector.exe setup       # 初回のみ。利用キー等をGUIダイアログで登録
KeibaDataCollector.exe morning     # 朝一: 当日の出走表取得→WordPress反映（※既存システムと重複、下記参照）
KeibaDataCollector.exe watch       # レース確定監視→結果・払戻を随時反映（※既存システムと重複、下記参照）
KeibaDataCollector.exe predict     # 朝一オッズの人気順から予想印を生成→反映（※既存システムと重複、下記参照）
KeibaDataCollector.exe score       # 6ファクター・AI指数を算出しscoresテーブルへ保存（WordPressへは送らない）
KeibaDataCollector.exe backfill    # 6ファクター用の過去データ取得（ローカルSQLiteのみ、WordPressへは送らない）
KeibaDataCollector.exe probe       # 調査用: どのデータ種別で何が取れるか確認（WordPressへ書き込まない）
KeibaDataCollector.exe dbstats     # 蓄積済みSQLiteの件数・日付範囲を確認
KeibaDataCollector.exe trend       # 本日の傾向（脚質・枠・馬場・上がり・通過順）を算出（ローカルのみ）
KeibaDataCollector.exe content     # 狙い馬・穴馬・危険な人気馬を生成・検証・公開（新規keiba_digest投稿へ）
KeibaDataCollector.exe verify      # predictionsを確定着順と突き合わせて検証（ローカルのみ）
KeibaDataCollector.exe healthcheck  # 更新が止まっていないか確認し、止まっていればメール通知
KeibaDataCollector.exe notifytest   # SMTP設定でテストメールを送信
KeibaDataCollector.exe backup      # historical.sqlite3のバックアップを作成（直近14世代保持、data\backup\）
KeibaDataCollector.exe stats       # 指数帯別の3着内率・勝率を表示
KeibaDataCollector.exe licensegate # LicenseGateの確認・更新（下記）
KeibaDataCollector.exe weights     # AI指数6ファクターの重み設定の確認・更新
KeibaDataCollector.exe dashboard   # 監視ダッシュボード（仕様書§17）をコンソール表示・WordPress送信
```

**本番運用で実際にスケジュール登録するのは `backfill incremental` / `score` / `trend` / `content` /
`verify` / `dashboard` のみ。** `morning` / `predict` / `watch` は既存システム
（`keiba-race-result-auto-posting` / `horse-race-custom-builder`）側の同名バッチが既に同じWordPress
投稿（`race`カスタム投稿タイプ）へ出走表・予想印・結果を反映しており、仕様書§2が求めているのは
既存の予想ページへの「連携」であって二重公開ではない。この新システムのTrend/Content/Verifyは
いずれもJV-Link/UmaConnから当日データを直接読み直す設計のため、`morning`/`predict`/`watch`が
このアプリ側で実行されている必要も無い。`morning`/`predict`/`watch`コマンド自体は残しているが
（既存システムを将来的にこちらへ統合する場合のため）、既存システムと同居させる運用では
Task Schedulerに登録しないこと（`register-scheduled-tasks.ps1`もこの3つは登録しない）。

運用（`deploy.ps1`によるビルド→再起動手順等）は既存2リポジトリと同じ運用スクリプトをそのまま
同梱している。詳細な注意点（JV-Link Setup取得は無人実行不可、ダイアログ対策、ページキャッシュ、
地方競馬のデータ欠損傾向等）は移植元リポジトリのREADMEに記載されている運用知見がそのまま当てはまる。

## VPSへの初回デプロイ手順

既存2システム（`keiba-race-result-auto-posting` / `horse-race-custom-builder`）と同じWindows VPS
常駐アプリとしての運用を前提にしている。既存2システムは実行ファイル名・タスク名とも
`KeibaDataCollector-*` の慣習を使っているため、本システムのタスクは既定で別名前空間
`KeibaAiDataSystem-*`（詳細は後述のTask Scheduler登録の節）を使い、衝突しないようにしている。
インストール先ディレクトリも既存2システムとは別にすること。

1. **前提ソフトウェアの導入**（VPS側で1回のみ）
   - .NET SDK（`dotnet build`用。net48ターゲットのビルドにはWindows上の .NET SDK が必要）
   - JV-Link（中央競馬）・UmaConn（地方競馬DATA、競馬最強の法則WEB）のクライアントソフトウェア。
     いずれも契約済みの利用キーが必要（仕様書§0・§22、JRA-VAN・競馬最強の法則WEB双方の手続き）
   - WordPress側で「アプリケーションパスワード」を発行しておく（`WordPressUser`/`WordPressAppPassword`
     用。ユーザー本人のパスワードではなくアプリケーションパスワードを使うこと）

2. **取得・ビルド**
   ```powershell
   git clone git@github.com:koppuchan/keiba-ai-data-system.git
   cd keiba-ai-data-system\src\KeibaDataCollector
   dotnet build -c Debug
   ```

3. **認証情報の設定**: `secrets.local.bat.example` を `secrets.local.bat` にコピーし、
   `WordPressUser` / `WordPressAppPassword` / `JvLinkSoftwareId` を実値に置き換える。
   **ASCII のみ・CRLF改行**を保つこと（`secrets.local.bat.example`冒頭のコメント参照。
   UTF-8や LF 改行だと `cmd.exe` が正しく読めず、値が反映されないまま後続処理が失敗する）。
   `secrets.local.bat` は `.gitignore` 対象のためコミットされない。

4. **JV-Link / UmaConnの初期設定**（対話操作が必要、GUIダイアログが開く）
   ```
   KeibaDataCollector.exe setup
   ```
   利用キーはこれを実行したWindowsユーザーのレジストリに保存されるため、以降の
   タスクスケジューラ登録（手順6）も同じユーザーで行うこと。

5. **WordPressプラグインの導入**: [`wordpress-plugin/keiba-ai-digest/`](../wordpress-plugin/keiba-ai-digest/README.md)
   の「インストール手順」を参照。導入直後は自動公開が既定でOFFになっている
   （LicenseGateと同じフェイルクローズ方針）。

6. **LicenseGateの状態確認**: JRA-VAN・競馬最強の法則WEB双方からの公開・商用利用許諾が
   確認できるまで、WordPressへの自動公開は設計上ブロックされる。許諾確認が取れ次第、
   `apply-licensegate.ps1` で承認済みの開催場を反映する（状態はSQLiteに永続化されるため、
   実行は初回と承認範囲が変わったときのみ。`deploy.ps1`には意図的に含めていない）。
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\apply-licensegate.ps1 -Note "2026-10-01 クライアントより許諾承認確認"
   ```
   個別の確認・更新は `licensegate show / set-jra / set-local`（詳細は後述）でも行える。

7. **Task Schedulerへの登録**（本README「Windows Task Scheduler登録」参照）
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\register-scheduled-tasks.ps1
   ```

8. **動作確認**: `probe` → `backfill`（全履歴、手動・対話実行）→ `dbstats` の順に、
   実際にデータが取得できているかを確認してから本番運用に入ることを推奨する。

以降のアップデート（`git pull`→再ビルド→タスク再登録）は個別に手順を追わず、
`deploy.ps1` を実行するだけでよい（停止→取得→ビルド→登録→再開を安全な順序でまとめて行う）。

## Windows Task Scheduler登録

`register-scheduled-tasks.ps1` が、仕様書§12の自動更新スケジュール表に沿って以下をすべて登録する。

**タスク名は既定で `KeibaAiDataSystem-*`。** 同じVPS上に既存2システム
（`keiba-race-result-auto-posting` / `horse-race-custom-builder`）が稼働している場合、
それらは同じ実行ファイル名・タスク名の慣習（`KeibaDataCollector-*`）を使っているため、
名前空間を分けている。このスクリプトは既存タスクと同名のタスクを見つけると上書き登録する
仕様なので、もし万一タスク名が重複すると既存システムの定義が書き換えられてしまう。
既定のプレフィックスのまま使い、変更する場合は既存タスク名と重複しない値にすること。

```powershell
powershell -ExecutionPolicy Bypass -File .\register-scheduled-tasks.ps1
```

| タスク | 既定時刻 | 内容 |
| --- | --- | --- |
| `KeibaAiDataSystem-HealthCheck` | 08:30〜（15分毎/14時間） | 更新停止の検知・メール通知 |
| `KeibaAiDataSystem-Backup` | 01:30（1回） | 深夜: DBのバックアップ（仕様書§12） |
| `KeibaAiDataSystem-BackfillIncremental` | 02:00（1回） | 深夜: 履歴データの差分取得 |
| `KeibaAiDataSystem-Morning` | 07:00（1回） | 早朝: 当日の出走表取得 |
| `KeibaAiDataSystem-Score` | 07:30〜（20分毎/14時間） | 朝〜発走前〜レース間: AI指数算出 |
| `KeibaAiDataSystem-TrendMorning` | 07:45（1回） | 朝: 事前想定傾向 |
| `KeibaAiDataSystem-Content` | 07:40〜（20分毎/14時間） | 発走前〜レース間: 狙い馬・穴馬・危険な人気馬の生成・検証・公開（Scoreの後に走るようずらしてある） |
| `KeibaAiDataSystem-Predict` | 09:00〜（15分毎/12時間） | 予想印の生成 |
| `KeibaAiDataSystem-TrendLive` | 09:00〜（30分毎/12時間） | 開催中: 現時点の傾向 |
| `KeibaAiDataSystem-Watch` | 09:30（1回、全確定で自動終了） | レース間: 結果・払戻監視 |
| `KeibaAiDataSystem-Dashboard` | 07:00〜（30分毎/15時間） | 日中: 監視ダッシュボードのWordPress送信 |
| `KeibaAiDataSystem-TrendFinal` | 21:30（1回） | 開催終了後: 本日の結果分析 |
| `KeibaAiDataSystem-Verify` | 22:00（1回） | 開催終了後: レース後検証 |

「前日夜: 翌日開催場・出走予定を準備」（仕様書§12）に対応する専用タスクは意図的に作っていない
（`morning`は当日分を早朝に取得すれば間に合う設計のため。詳細はスクリプト冒頭のコメント参照）。

既存システムの `KeibaDataCollector-Watch` 等が既に稼働中の場合、実行タイミングが重なると
JV-Link/UmaConnへの同時接続で競合する可能性がある。本システムの既定時刻（Watch=09:30〜等）と
既存システム側の登録時刻を見比べ、必要なら `-WatchTime` 等の引数でずらすこと。

## LicenseGate（仕様書§5）

「データを取得できる」ことと「そのデータをWebで公開できる」ことを明確に分離するための永続化層
（`Data/LicenseGateStore.cs`）。

- JRA-VAN（中央競馬）は全国一律の契約のため `jra_license` テーブルに1行のみ
- 競馬最強の法則WEB / 地方競馬DATA（UmaConn）は競馬場ごとに契約が分かれるため
  `local_venue_license` に venue_id ごとの行
- **未設定・行が存在しない場合は常に非公開（フェイルクローズ）**。取得可否はこの判定に一切関与しない

```
KeibaDataCollector.exe licensegate show
KeibaDataCollector.exe licensegate set-jra active approved approved "2026-xx-xx JRA-VANより書面確認"
KeibaDataCollector.exe licensegate set-local 35 盛岡 active approved "2026-xx-xx 契約書PDF確認済み"
KeibaDataCollector.exe licensegate check 35 local
```

## AI指数エンジン（仕様書§8・§9）

`score`コマンド実行時に、6ファクター算出（`FactorScoringService`）に加えて`Services/AiIndexService.cs`
が単一の「AI指数」へ加重平均で統合し、`scores`テーブルへ保存する。

- 加重平均は **Σ(値×重み) / Σ(重み)**（算出できた＝nullでないファクターのみ対象）。単純合計では
  ないので、算出できたファクター数が多い馬が自動的に有利になることはない
- 重みは`score_weights`テーブルのDB設定値。セグメント（`central`/`local` × `turf`/`dirt`、
  例: `central:turf`）ごとに個別設定でき、未設定なら`default`→全項目1.0の順にフォールバックする
- `model_version`（採点式のバージョン）・`feature_version`（特徴量抽出ロジックのバージョン）・
  `data_cutoff_utc`（その算出が見たデータの基準時刻）を毎回の算出結果に必ず付与する
- 取消・除外馬（SEレコードの異常区分コード≠0）はAI指数TOP5から自動的に除外される
- AI指数TOP5は開催場（開催日×競馬場）全体で上位5頭。同点はデータ充足率→馬番の順でタイブレーク

- 競技種別ごとにモデルを分けられる構造にしている: ばんえい競馬（場コード83）は重みのsegmentが
  `banei`（`weights set banei ...`で平地とは別に設定可能）、モデルバージョンが`ai-index-v1-banei`となり、
  検証（`stats`・サイト上の検証ページ）でも平地と混ざらない。専用の指標・重みが用意できるまでは
  既定の重みで算出した参考値で、公開ページにもその旨の注記を表示する

```
KeibaDataCollector.exe weights show central:turf
KeibaDataCollector.exe weights set central:turf 1.2 1.0 1.0 1.5 0.8 1.0
```

`score`コマンド実行時点ではまだWordPressへ送らず、コンソールへログ出力するのみ。実際の公開は
`content`コマンド実行時に`DigestPublisherService`がまとめて行う（後述）。

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
- `morning`段階の通過順傾向（最終コーナー先頭馬の勝率）は`race_entries.final_corner_leader`列
  （`BackfillService`が最終コーナー通過順位から算出して保存する）を母集団にする。この列が無い
  旧DB・再backfill前の行は対象から自然に除外されるため、`run-backfill.bat`を再実行していない
  環境ではサンプル数が少なく出る点に注意

`trend`コマンド実行時点ではまだWordPressへ送らない。`morning`/`live`/`final`いずれの段階も
`trend_snapshots`に保存されるだけで、`content`コマンド実行時にその時点で保存済みの最新スナップショットを
`DigestPublisherService`がまとめて公開する。

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
  対応）。騎手変更・馬場変更の検知はValidatorが`scores`テーブルの現在の値と突き合わせて行う

```
KeibaDataCollector.exe content
KeibaDataCollector.exe content 2026-08-30
```

## Validator（仕様書§14・§5 LicenseGate本接続）

`content`コマンド実行時、生成された各GeneratedPickは公開前に`ValidatorService`を必ず通る。

- **DB再照合**: 生成時点で参照していたAI指数・血統登録番号・馬名・騎手コード・馬場状態コードを、
  今の`scores`テーブルの値と突き合わせる。対象馬が取消・除外になっていたり、血統登録番号や馬名が
  一致しない（馬番の入れ替わり等）、AI指数が許容誤差（±2.0）を超えて変化していれば不合格にする。
  **騎手変更・馬場変更**（仕様書§15・§21）もここで検知し不合格にする（即時の自動再計算は
  トリガーしない。`score`/`content`は1日に複数回の再実行が前提の設計のため、次回実行で新しい値を
  反映した予測が自然に生成される）。馬名はSEレコードの`Bamei`を`scores.horse_name`へ保存したものを
  使う（独立した馬マスタテーブルは無く、あくまでketto_numが一意キーで馬名は補助的な照合・表示用）。
  馬場状態コード（芝ならSibaBabaCD、ダートならDirtBabaCD）はAI指数6ファクター自体の入力には使わず、
  記録・照合専用
- **LicenseGate本接続**: `LicenseGateStore.IsWebPublishAllowed(venueId, isCentral)` を呼び、
  この開催場が今Web公開してよい状態かを判定する
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
3. **Validator未通過ピックの除外**: `content`コマンド内で既にValidatorを通している。
   不合格だったピックはpublish対象に含めない
4. **空コンテンツなら送信しない**: AI指数TOP5・傾向・ピックが全て空ならWordPressへ何も送らない
   （仕様書§13「更新失敗時に空ページ・壊れたページを出さない」と同じ考え方をpublish要否にも適用）

WordPress側の対応プラグインは[`wordpress-plugin/keiba-ai-digest/`](../wordpress-plugin/keiba-ai-digest/)。
**自動公開の既定値はOFF**（LicenseGateと同じフェイルクローズ方針）。§13の「手動再実行」は、
VPS上で`run-score.bat`/`run-content.bat`を対象日指定で再実行する運用のままにしている
（WordPress側からVPSの処理を起動する経路は、攻撃面を増やさないため意図的に作っていない）。

## レース後の自動検証（仕様書§18 Verification DB）

`verify`コマンドで、`predictions`テーブル（狙い馬/穴馬/危険な人気馬はValidatorが、AI指数TOP5は
DigestPublisherが、それぞれ公開のタイミングでimmutableにsnapshot済み）を当日の確定着順と突き合わせ、
`verification`テーブルへ記録する。

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

**サイトでの公開**: `verify`の最後（および`verifyreport`コマンド）が、検証結果を集計してWordPressへ送り、
プラグインが固定ページ「AI指数の過去検証」（`/ai-digest-verification/`、ショートコード
`[keiba_ai_verification]`）を自動作成して表示する。自動公開がOFFの間は送らない。表示内容:
AI指数TOP5・1位指数馬の成績、カテゴリ別、指数帯別、中央/地方別、競馬場別、芝/ダート別、集計期間、
対象レース数（母数が30未満の行は「参考」と表示）。数字を良く見せられないよう、予測は公開時点の
AI指数のまま記録（predictionsはInsert専用）し、同じ予測が再生成されても最初に公開した1件だけを
集計する。モデルバージョンごと（ばんえいは別モデル）に分けて集計する。

`stats`コマンドで、AI指数を10点刻みの帯に分けた3着内率・勝率を確認できる（仕様書§18「指数帯別成績を
model_versionごとに分離」）。

```
KeibaDataCollector.exe verify
KeibaDataCollector.exe verify 2026-08-30
KeibaDataCollector.exe stats Nerai ai-index-v1
KeibaDataCollector.exe stats AiIndexTop5 ai-index-v1
```

## エラー処理・監視ダッシュボード（仕様書§15・§17）

仕様書§15のエラー処理表に対する対応状況:

| エラー | 仕様書の処理 | 対応 |
|---|---|---|
| データ取得失敗 | 再試行→失敗なら公開停止＋通知 | JV-Link/UmaConnの読み取りは各サービスが例外を投げ、`Program.LogFailure`が監査ログへ記録（WordPress送信は元から自動再試行あり） |
| 重要項目欠損 | 公開停止 | Validatorが対象馬未検出・AI指数算出不能を不合格にする |
| 馬番不一致 | 公開停止 | Validatorが血統登録番号の不一致で不合格にする |
| 騎手変更 | 再計算 | Validatorが`scores.jockey_code`との不一致を検知して公開停止。次回の`score`/`content`実行で自然に再計算される |
| 取消/除外 | ランキングから除外 | AiIndexService/ContentGeneratorServiceが対応済み |
| オッズ異常 | 穴馬判定停止 | `JvRecordParser.ParseTanshoOdds`が0円・非数値のオッズを除外済み、穴馬判定もオッズ未取得時は判定しない |
| AI生成失敗 | テンプレートへフォールバック/停止 | この実装はそもそも自由文生成AIを呼ばない（仕様書§14）ため、失敗しうるのは「対象馬なし」のみで、その場合は単にピックを生成しない |
| WordPress API失敗 | 再試行＋通知 | `WordPressClient`が最大4回再試行済み。再試行後も失敗すれば例外化し`LogFailure`経由で監査ログへ |
| LicenseGate未承認 | 公開処理を強制停止 | `DigestPublisherService`/`ValidatorService`が対応済み |

「通知」は`AuditLogStore`（`audit_logs`テーブル）への記録が必須部分。加えて`NotifierService`が
severity=Criticalのものだけメール送信する（SMTP未設定なら送信自体を行わない）。Criticalにしているのは
データ取得（バックフィル）失敗、AI指数算出失敗、コンテンツ生成・WordPress公開失敗、起動失敗、
および後述の更新停止。同じ原因のメールは60分以内に再送しない（20分おきのタスクが障害中に
同じ内容を送り続けるのを防ぐため）。

**メール設定**（`secrets.local.bat`に`SmtpHost`/`SmtpPort`/`SmtpUseSsl`/`SmtpUser`/`SmtpPassword`/
`SmtpFrom`/`SmtpTo`。例は`secrets.local.bat.example`）。465番は接続と同時にTLS、587番はSTARTTLS。
設定後は`KeibaDataCollector.exe notifytest`でテストメールを送って確認する。
**更新停止の検知**: `KeibaAiDataSystem-HealthCheck`（08:30〜15分おき）が`healthcheck`を実行し、
開催日の日中にAI指数算出またはWordPress公開が60分以上成功していなければ通知する
（再通知は2時間おき）。一時的な失敗ではなく「止まった状態が続く」場合に気付くための仕組み。

`Program.LogFailure`はすべてのコマンドの例外処理が最終的に通る1箇所のため、ここに集約することで
各コマンドを個別に手直しせず横断的にエラーを記録している。成功時も`LogSuccess`で`audit_logs`へ
記録し、「エラーが無い」と「一度も実行されていない」を区別できるようにしている。

`dashboard`コマンドで仕様書§17の監視ダッシュボード項目（当日開催場、最終データ同期時刻、
最終AI計算時刻、最終WordPress更新時刻、データソース接続状態、LicenseGate状態、未処理レース数、
エラー件数、公開停止理由、自動公開ON/OFF）をコンソール表示し、WordPress側
（`wordpress-plugin/keiba-ai-digest`の設定画面）へも送信する。「手動再実行」は運用手順の案内表示のみ
（VPS上で対象のbatを再実行する既存の運用のまま）。

```
KeibaDataCollector.exe dashboard
KeibaDataCollector.exe dashboard 2026-08-30
```

## 既知の制約

- **クッション値が取得できない**: 仕様書§3が求める「クッション値」に対応するJV-Dataのdataspec/
  フィールドが、現在組み込んでいる`JVData_Struct.cs`（JRA-VAN公式SDK）内に見当たらない。地方競馬
  DATA側に別途存在する可能性はあるが、推測でdataspec名を決め打ちしない方針のため未対応
- **`venues`/`horses`/`results`の専用マスタテーブルは無い**: 仕様書§7は11テーブルを挙げているが、
  本実装では`track_code`文字列をキーに扱い、馬名は`scores`/`predictions`の各行に非正規化して保存する
  設計にしている。`ketto_num`が一意キーであることに変わりはない。レース結果もWordPress側
  （`race`投稿の`race_result`メタ）が正データで、検証時（`verify`コマンド）はJV-Link/UmaConnから
  当日分を都度読み直す。機能的には代替できているが、正規化された完全なスキーマではない
- **地方競馬の馬場種別判定（ばんえい等）**: `AiIndexService.BuildSegment`はTrackCDの先頭1桁で
  芝/ダートを判定し、該当しない場合（ばんえい等）は開催場単位の重み設定にフォールバックする。
  専用の指標を別途設計する仕様書§2の要求までは対応していない

## ビルドについて

JV-Link/UmaConn連携を前提に `PlatformTarget=x86` / `net48` で構成している。**.NET Framework 4.8の
ビルド環境はWindowsが前提**で、この環境（Linux）では `dotnet build` を検証できていない。
Windows VPS側で初回ビルドを確認すること。

## 機密情報の扱い

利用キー・WordPress認証情報等は環境変数で注入する方針（`AppConfig.cs`参照）。
`App.config` には非機密の既定値のみをコミットする。LicenseGateの承認状態そのものは機密ではないが、
保存先の `historical.sqlite3` はgit管理対象外（`.gitignore`参照）。
