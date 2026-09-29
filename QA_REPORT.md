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
| 6 | 取消・騎手変更・馬場変更が反映される | ⚠️ 部分対応 | 取消✅（Issue #3・#5）、騎手変更✅（Issue #9で追加）。**馬場変更（当日の馬場状態変化）は未対応**。理由: AI指数6ファクターはそもそも「当日の馬場状態」を入力に使っていない（枠・馬場バイアスは経路依存の構造的傾向であり、リアルタイムの馬場状態とは別物）ため、現状の採点モデルには「陳腐化しうる馬場状態」が組み込まれていない。Trend Engineの天候・馬場表示は毎回当日データを直接読み直すため、そちらは常に最新（陳腐化しない）。真に対応するには採点モデル自体の拡張が必要で、本Issueの範囲を超えるため次フェーズ送りとした |
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
- **馬名でのValidator照合が未対応**: 表示名を持つ馬マスタテーブルがこのシステムに無いため、より強い一意キーであるketto_num（血統登録番号）で代用している（Issue #6で既知の制約として明記済み）。
- **`venues`/`horses`/`results`の専用マスタテーブルが無い**: 仕様書§7は11テーブルを挙げているが、本実装では`venues`（開催場マスタ）と`horses`（馬名マスタ）を独立テーブルとして持たず、`track_code`文字列と`ketto_num`をキーとして扱う設計にした。`results`も同様に専用テーブルを持たず、レース結果はWordPress側（`race`投稿の`race_result`メタ）が正データで、検証時（`verify`コマンド）はJV-Link/UmaConnから当日分を都度読み直す設計にした（`historical.sqlite3`への当日結果のリアルタイム反映が無いため）。機能的には代替できているが、正規化された完全なスキーマではない。
- **Trend Engineの朝段階の通過順傾向が算出できない**（Issue #4で既知の制約として明記済み）。
- **地方競馬の馬場種別判定（ばんえい等）**: `AiIndexService.BuildSegment`はTrackCDの先頭1桁で芝/ダートを判定し、該当しない場合（ばんえい等）は開催場単位の重み設定にフォールバックする。専用の指標を別途設計する仕様書§2の要求までは対応していない。

## 未実施（実機が必要なため、このセッションでは検証不能）

- .NET Framework 4.8 / x86でのビルド確認（`dotnet build`）
- 実際のJV-Link/UmaConn接続でのSource Adapter動作確認
- WordPress REST APIへの実際の送信確認（`keiba-ai-digest`プラグインの動作含む）
- PHPの構文チェック（この環境にphp-cliが無いため未実施。手動レビューのみ）
- JRA-VAN・競馬最強の法則WEB双方からの公開・商用利用許諾確認（お客様側タスク。仕様書§0・§22）

## 推奨する次のステップ

1. 全PR（#11〜このIssueのPRまで）をレビューし、順にマージする
2. Windows機（またはWindows上のCI）で`dotnet build`を実行し、コンパイルエラーを解消する
3. VPS上でJV-Link/UmaConnの利用キーを設定し、`probe`コマンドで実際にデータが取れるか確認する
4. WordPressにテーマ側の表示（`keiba_digest`投稿・`race`投稿の見せ方）を実装する
5. 許諾確認が取れるまでLicenseGateを`pending`のままにし、内部分析用途（`licensegate`/`weights`/`dashboard`等のCLI確認）に限定して動作確認する
