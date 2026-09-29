# 開発計画 — JRAVAN＋競馬最強の法則WEB 全自動AI競馬データシステム

前提: フルスコープ（MVP＋狙い馬/穴馬/危険な人気馬＋レース後検証）を1〜10日で構築する。
既存2リポジトリ（`keiba-race-result-auto-posting` / `horse-race-custom-builder`）を土台に fork・拡張する方式とし、ゼロから作る部分を最小化する。

## 0. 前提条件（着手前ブロッカー）

- **LicenseGate用の許諾確認はお客様側タスク**。JRA-VAN・競馬最強の法則WEB双方の「Web公開許諾」「商用利用許諾」が確認できるまで、該当場のWordPress自動公開は実装完了後も停止状態のままにする（内部分析用途は許諾状態と無関係に可）。
- 開発環境: 既存と同じくWindows（x86）常駐アプリ＋WordPress。JV-Link/UmaConnの利用キーは契約済みのものをそのまま使う想定。

## 1. アーキテクチャ／既存資産の再利用マップ

仕様書§6のパイプライン: `Source Adapter → Raw Store → Normalizer → Feature Engine → AI Scoring Engine → Trend Engine → Content Generator → Validator → WordPress Publisher → Result Collector → Verification DB`

| コンポーネント | 対応する既存コード | 扱い |
|---|---|---|
| Source Adapter | `Interop/JvSpecComDataSource.cs`（JV-Link/UmaConn共通アダプタ） | ほぼそのまま流用 |
| Raw Store | `Data/HistoricalDataStore.cs`（`historical.sqlite3`） | 流用＋テーブル追加（§2参照） |
| Normalizer | `RaceDiscovery.cs` / `JvRecordParser.cs` / `JvFactorRecordParser.cs` | 流用。共通ID化ロジックは既存のvenue/race_id生成をそのまま使う |
| Feature Engine | `JvFactorRecordParser`の特徴量抽出部（early_position_ratio等） | 流用 |
| AI Scoring Engine | `Services/FactorScoringService.cs`（6ファクター実装済み） | ほぼそのまま流用。model_version/feature_version/data_cutoffの永続化のみ追加 |
| Trend Engine | なし | **新規**（§10 本日の傾向、3段階） |
| Content Generator | なし（`hrc_is_race_visible`等の可視性判定はあるが文章生成はなし） | **新規**（§11 狙い馬・穴馬・危険な人気馬） |
| Validator | なし | **新規**（§14 生成後にDB照合、不一致なら公開停止） |
| WordPress Publisher | `WordPress/WordPressClient.cs` ＋ `keiba-race-sync`プラグイン | 流用＋LicenseGate/Validatorのゲート追加 |
| Result Collector | `Services/RaceResultService.cs`（ポーリング方式） | 流用 |
| Verification DB | 一部（factor集計のみ）→ 予測snapshot永続化は未実装 | 拡張（§18 immutable snapshot） |
| LicenseGate | なし | **新規**（§5、最優先） |
| Monitoring | なし（ログのみ） | **新規**（§17、簡易ダッシュボード） |

## 2. データモデル拡張（`historical.sqlite3`に追加）

既存DBは6ファクター集計用のテーブルが中心。仕様書§7の11テーブルのうち、無いものを追加する。

| 追加テーブル | 主キー | 用途 |
|---|---|---|
| `license_gate` | venue_id | JRA取得/JRA Web公開許諾/地方データ契約/地方Web公開許諾の状態（active・pending・approved・rejected） |
| `predictions` | prediction_id | 狙い馬/穴馬/危険な人気馬の生成結果。予測時点のオッズ・馬場・指数・根拠をimmutableで保存 |
| `verification` | prediction_id | レース後の着順・的中判定。predictionsは書き換えず別テーブルで突合 |
| `trend_snapshots` | date+venue_id+stage | 朝/開催中/終了後の傾向集計スナップショット |
| `audit_logs` | event_id | 取得・計算・公開・エラーの履歴（§16セキュリティ、§17監視の元データ） |

既存の `race_entries` / `pedigree_links` 等はそのまま維持。WordPress側の `race_card` 等のpost meta JSONは表示用の派生データとして残し、正データはDB側に寄せる（仕様書§4「原データDBと公開用派生データを分離」に合わせる）。

## 3. 日別スケジュール（Day1〜10）

| Day | フェーズ対応(§19) | 内容 |
|---|---|---|
| 1 | Phase 0 + LicenseGate土台 | `license_gate`テーブル作成、管理画面（or 設定ファイル）でactive/pending/approved/rejectedを切替可能に。公開経路の先頭にゲートを差し込む土台だけ先に作る（実際のチェック処理はDay6） |
| 2 | Phase 1-2 | Source Adapter/Normalizerの流用確認、開催場自動判定ロジックの動作確認（JV-Link/UmaConn両方）、共通ID化の漏れ確認 |
| 3 | Phase 3 | AI Scoring Engine配線。`FactorScoringService`をAPI/サービス化し、model_version・feature_version・data_cutoffを`scores`テーブルに保存するよう変更 |
| 4 | Phase 4 | Trend Engine新規実装。朝/開催中/終了後の3段階集計、最低サンプル数未満のレースは断定しないロジック |
| 5 | Phase 5 | Content Generator新規実装。構造化データのみを入力にした狙い馬・穴馬・危険な人気馬のテンプレート文生成。「絶対」「確実」等の断定表現の禁止ワードチェックを含める |
| 6 | Phase 6 | Validator実装。生成後に馬名・馬番・指数・レース番号をDBと照合、不一致なら公開停止。LicenseGateの実チェックをPublisher直前に接続 |
| 7 | Phase 7 | WordPress Publisher拡張。idempotent公開（同一日・同一コンテンツの重複防止）、更新失敗時に空ページ/壊れたページを出さないフォールバック、管理画面に公開ON/OFFと手動再実行を追加 |
| 8 | Phase 8-9 | Result Collector拡張、Verification DB本実装。予測snapshotをimmutableに保存し、レース後に着順・3着内率をmodel_version別に自動集計 |
| 9 | Phase 10（一部） | エラー処理（§15の表）の実装、監視ダッシュボード（当日開催場・最終同期時刻・LicenseGate状態・未処理レース数・公開停止理由・手動再実行） |
| 10 | 総仕上げ・QA | §21受け入れ基準の一通り確認、ステージング確認、許諾確認状況の再確認、引き継ぎメモ作成 |

## 4. リスク・スコープ調整メモ

- 1〜10日という期間はこの資産流用を前提にしても攻めた日程。もし遅延が出る場合は **Trend EngineとContent Generatorの文章テンプレートを簡素化**（パターン数を絞る）ことで吸収する。AI指数TOP5・LicenseGate・Validatorは削らない（仕様書の必須事項のため）。
- 地方競馬（UmaConn）のデータ欠損（単勝オッズが入らない競馬場、ばんえいの斤量等）は、既存READMEに記録済みのフォールバック処理（0B31からの補完等）をそのまま踏襲し、新規調査はしない。
- LicenseGateの許諾確認自体はお客様側の対外手続きに依存するため、実装が完了しても許諾が取れていない場（venue）は自動的に非公開のまま運用開始する。

## 5. 受け入れ基準チェックリスト（§21ベース）

- [ ] 中央・地方とも当日の開催場を手動入力せず判定できる
- [ ] 契約範囲外の競馬場を誤って公開しない（LicenseGate）
- [ ] 天候・馬場・指数に取得時刻が付く
- [ ] AI指数TOP5が自動更新される
- [ ] AIコメントが元データと一致する（Validator）
- [ ] 取消・騎手変更・馬場変更が反映される
- [ ] 予測snapshotを変更せず結果検証できる
- [ ] データ障害時に古い情報を最新情報として誤表示しない
- [ ] LicenseGate未承認時は公開ジョブが実行されない
- [ ] WordPress更新失敗時に通知される
- [ ] 同じジョブを複数回実行しても重複公開されない
