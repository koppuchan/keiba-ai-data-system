# QA・受け入れ基準チェック（Issue #10）

Issue #1〜#9で実装した内容を、仕様書§21受け入れ基準およびDEVELOPMENT_PLAN.md §5チェックリストに
照らして確認した記録。実機（Windows/JV-Link/UmaConn/WordPress）でのビルド・動作確認はできていない
（このセッションはLinux上で行っており、そもそも.NET Framework 4.8のビルドができない）ため、
**コードレビューによる静的な確認**が中心。実機確認はマージ前に別途必要。

## 仕様書§21 受け入れ基準

| # | 基準 | 状態 | 根拠 |
|---|---|---|---|
| 1 | 中央・地方とも当日の開催場を手動入力せず判定できる | ✅ | `RaceDiscovery.ForDate`（Issue #2） |
| 2 | 契約範囲外の競馬場を誤って公開しない | ✅ | `LicenseGateStore.IsWebPublishAllowed`がフェイルクローズ（Issue #1・#6・#7） |
| 3 | 天候・馬場・指数に取得時刻が付く | ✅ | `scores.computed_at_utc`／`VenueTrendSnapshot.ComputedAtUtc`（天候・馬場情報を含む） |
| 4 | AI指数TOP5が自動更新される | ✅ | `ScoresStore.GetVenueTop5`（Issue #3） |
| 5 | AIコメントが元データと一致する | ✅ | `ValidatorService`が生成時値とDBを再照合（Issue #6） |
| 6 | 取消・騎手変更・馬場変更が反映される | ✅ | 取消✅（Issue #3・#5）、騎手変更✅（Issue #9）、馬場変更✅（Issue #14で追加）。馬場変更検知は騎手変更と同じパターン（`scores.baba_condition_code`を生成時と現在で突き合わせ、変化していれば公開停止）。**AI指数6ファクター自体は馬場状態を入力に使っていない**（枠・馬場バイアスは経路依存の構造的傾向であり、リアルタイムの馬場状態とは別物）ため、この対応は指数計算を馬場状態対応にするものではなく、「算出時点から馬場状態が変わった」という事実を古い情報のまま公開しないための記録・照合専用ゲート |
| 7 | 予測snapshotを変更せず結果検証できる | ✅ | `PredictionStore`はInsert専用、`VerificationStore.Upsert`も未確定→確定の一方向のみ（Issue #6・#8） |
| 8 | データ障害時に古い情報を最新情報として誤表示しない | ✅ | 部分的な失敗時は`DigestPublisherService`が送信自体を行わない（＝直前の正しい内容がWordPress側に残る）。`updated_at`メタで最終更新時刻を確認可能 |
| 9 | LicenseGate未承認時は公開ジョブが実行されない | ✅ | `DigestPublisherService.PublishAsync`の最初のゲート（Issue #7） |
| 10 | WordPress更新失敗時に通知される | ✅（本Issueで修正） | 従来はWordPress公開失敗が`LogFailure`のデフォルト（critical=false）に埋もれ、メール通知（Critical限定）が飛ばない不備があった。`RunContentFor`のPublishAsync呼び出しと、`RunWatchFor`の「打ち切り時刻までに監視が回復しなかった」経路をそれぞれ`critical: true`に変更 |
| 11 | 同じジョブを複数回実行しても重複公開されない | ✅ | `digest_key`によるidempotent upsert（Issue #7） |

## 本Issueで見つけて直した不具合

1. **WordPress公開失敗が通知されない**: `RunContentFor`内の`DigestPublisherService.PublishAsync`呼び出しを個別のtry/catchで囲み、失敗時は`critical: true`でログ・通知するように変更（受け入れ基準#10）。1開催場の公開失敗で他の開催場を巻き添えにしない設計は維持。
2. **watch監視が打ち切り時刻までに回復しなかった場合に通知されない**: 従来は`Console.WriteLine`のみで静かに終了していた。運用者が気付けるよう`critical: true`で`LogFailure`するように変更。

## その他、確認した既知の制約（新規の不具合ではなく、設計上の判断として据え置き）

- **クッション値が取得できない**: 仕様書§3が求める「クッション値」に対応するJV-Dataのdataspec/フィールドを、現在組み込んでいる`JVData_Struct.cs`（JRA-VAN公式SDK）内に確認できなかった。地方競馬DATA側に別途存在する可能性はあるが、推測でdataspec名を決め打ちしない方針（既存READMEの一貫した方針）のため、未対応のまま明記する。
- ~~馬名でのValidator照合が未対応~~ → Issue #13で対応済み（`scores`/`predictions`に`horse_name`列を追加し、SEレコードの`Bamei`をValidatorが再照合するようになった。独立した馬マスタテーブルは無く、ketto_numが引き続き一意キー、馬名は補助的な照合・表示用）。
- **`venues`/`horses`/`results`の専用マスタテーブルが無い**: 仕様書§7は11テーブルを挙げているが、本実装では`venues`（開催場マスタ）と`horses`（馬名マスタ）を独立テーブルとして持たず、`track_code`文字列をキーとして扱い、馬名は`scores`/`predictions`の各行に非正規化して保存する設計にした（Issue #13）。`ketto_num`が一意キーであることに変わりはない。`results`も同様に専用テーブルを持たず、レース結果はWordPress側（`race`投稿の`race_result`メタ）が正データで、検証時（`verify`コマンド）はJV-Link/UmaConnから当日分を都度読み直す設計にした（`historical.sqlite3`への当日結果のリアルタイム反映が無いため）。機能的には代替できているが、正規化された完全なスキーマではない。
- ~~Trend Engineの朝段階の通過順傾向が算出できない~~ → Issue #12で対応済み（`race_entries.final_corner_leader`列を追加し、再backfill後は朝段階でも算出できるようになった）。
- **地方競馬の馬場種別判定（ばんえい等）**: `AiIndexService.BuildSegment`はTrackCDの先頭1桁で芝/ダートを判定し、該当しない場合（ばんえい等）は開催場単位の重み設定にフォールバックする。専用の指標を別途設計する仕様書§2の要求までは対応していない。

## 未実施（実機が必要なため、このセッションでは検証不能）

- .NET Framework 4.8 / x86でのビルド確認（`dotnet build`）
- 実際のJV-Link/UmaConn接続でのSource Adapter動作確認
- WordPress REST APIへの実際の送信確認（`keiba-ai-digest`プラグインの動作含む）
- PHPの構文チェック（この環境にphp-cliが無いため未実施。手動レビューのみ）
- JRA-VAN・競馬最強の法則WEB双方からの公開・商用利用許諾確認（お客様側タスク。仕様書§0・§22）

## 推奨する次のステップ

1. 全PRをレビューし、順にマージする（PR番号は#11〜#20から#21〜#30へ振り直し済み。詳細は各PRのコメント参照）
2. Windows機（またはWindows上のCI）で`dotnet build`を実行し、コンパイルエラーを解消する
3. VPS上でJV-Link/UmaConnの利用キーを設定し、`probe`コマンドで実際にデータが取れるか確認する
4. ~~WordPressにテーマ側の表示を実装する~~ → `keiba_digest`投稿側はIssue #11（フロント表示）で対応済み。`race`投稿側は既存`keiba-race-sync`プラグインの表示をそのまま使う
5. 許諾確認が取れるまでLicenseGateを`pending`のままにし、内部分析用途（`licensegate`/`weights`/`dashboard`等のCLI確認）に限定して動作確認する

## 追記（Issue #11: フロント表示）

`keiba_digest`投稿の個別ページに、AI指数TOP5・本日の傾向・狙い馬/穴馬/危険な人気馬を自動表示する
`the_content`フィルタを追加した。実装の過程で、`WordPressClient`のJSON送信設定（`CamelCaseSettings`）に
列挙型（`PickCategory`/`TrendStage`）の変換設定が無く、整数値でシリアライズされてしまう不具合を発見・
修正した（`StringEnumConverter`を追加。`predictions.category`列が既に`Category.ToString()`で
文字列保存していたのと矛盾していた）。詳細は同Issueのプラグイン`README.md`「表示側の既知の制約」を参照。

## 追記（Issue #12〜#14: 残っていたギャップへの対応）

本レポート公開後、Trend Engine朝段階の通過順傾向（Issue #12）、Validatorの馬名照合（Issue #13）、
Validatorの馬場変更検知（Issue #14）の3件に追加対応した。詳細は上表・各Issueの説明を参照。

## 追記（仕様書の全体再チェックとIssue #15）

ユーザーの依頼で仕様書PDFを§1から§22まで再度通読し、実装と突き合わせた。見つかったギャップは2件。

1. **§12自動更新スケジュールが未実現だった（対応済み）**: `register-scheduled-tasks.ps1`はIssue #2の
   移植時点のままMorning/Predict/Watchの3タスクしか登録しておらず、Issue #3〜#14で追加した
   score/trend/content/verify/dashboard（および移植元に元々あった`backfill incremental`）は
   `scheduled-*.bat`が存在するのにタスクスケジューラへの登録が無かった。運用者が手動実行しない限り
   AI指数算出・傾向分析・コンテンツ生成・検証・監視ダッシュボード更新が「全自動」になっていない
   状態だった。Issue #15で8タスクを追加登録し、仕様書§12の時期区分（前日夜/早朝/朝/発走前/
   レース間/開催終了後/深夜）に沿った時刻に割り当てた（詳細はKeibaDataCollector README参照）。
2. **§2「WordPress: 既存サイトの予想ページとスマートAI予想ジェネレータへ連携」（確認済み・対応不要と判明）**:
   コードには名称が登場しなかったため一度は要確認事項としたが、実際に
   `https://www.keiba-tips.top/smartaigenerator/` を確認したところ、ページタイトルが
   「スマートAI予想ジェネレーター生成ページ」で、内容は`horse-race-custom-builder`の
   `[keiba_custom_builder]`ショートコード（My総合指数・6ファクターのインタラクティブ採点）
   そのものだった。つまり「スマートAI予想ジェネレータ」＝`horse-race-custom-builder`の
   顧客向けブランド名であり、別の未知の機能ではなかった。本実装はIssue #2で
   `FactorScoringService`/`FactorPublishService`を無変更で移植し、`hrc_factors`メタを
   従来と同じ形式でpublishし続けている（新機能はすべて別の`keiba_digest`投稿タイプに
   実装したため、`race`投稿の`hrc_factors`には触れていない）。よって連携は元から維持
   されており、追加対応は不要と判断した。

未対応のまま残っているのはクッション値と`venues`/`horses`/`results`の専用マスタテーブルの2点のみ。
