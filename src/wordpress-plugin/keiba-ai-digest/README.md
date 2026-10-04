# Keiba AI Digest（WordPress companion プラグイン）

`KeibaDataCollector`の`content`コマンドが算出・検証した、AI指数TOP5・本日の傾向・狙い馬/穴馬/危険な
人気馬を受け取り、開催場・日単位のカスタム投稿タイプ`keiba_digest`として保存・表示する。

既存の`keiba-race-sync`（`race`投稿、1レース単位）とは別の投稿タイプ。こちらは1開催場・1日で1投稿。

## できること

- カスタム投稿タイプ`keiba_digest`を登録（`show_in_rest: true`、archive `/ai-digest/`）
- `digest_key` / `race_date` / `track_code` / `ai_index_top5` / `trend_morning` / `trend_live` /
  `trend_final` / `picks` / `license_visible` / `updated_at` をREST経由で読み書き許可
- `GET /wp-json/wp/v2/keiba_digest?meta_key=digest_key&meta_value=xxxx` で既存投稿を検索可能にする
  （`WordPressClient.FindDigestPostIdAsync`が使用）
- `GET /wp-json/keiba-ai/v1/settings` で自動公開ON/OFFを返す（`WordPressClient.IsAutoPublishEnabledAsync`
  がpublish前に毎回確認する）。`POST`（要管理者権限）で更新可能
- `POST /wp-json/keiba-ai/v1/status`（要`edit_posts`権限、収集アプリのApplication Password想定）で
  監視ダッシュボード（仕様書§17）のスナップショットを受け取り、`GET`（要管理者権限）で参照可能
- 設定 → Keiba AI Digest 画面から自動公開ON/OFFの切り替え、および最新の監視ダッシュボードを確認可能
- `keiba_digest`投稿の個別ページで、AI指数TOP5・本日の傾向・狙い馬/穴馬/危険な人気馬を自動表示
  （本日の傾向は朝・開催中・終了後の保存済みの段階をすべて、各段階の取得時刻付きで表示。
  天候・馬場状態はJV-Dataコード表（2011/2010）の名称（晴・曇・良・稍重等）で表示し、
  未設定（0）の間は該当項目を表示しない。最終更新時刻も表示）
  （`the_content`フィルタで生成。`keiba-race-sync`と同じ方針でエディタ本文は使わない）
- AI指数TOP5・狙い馬/穴馬/危険な人気馬には馬番だけでなく馬名も表示

## 自動公開ON/OFFの既定値

**既定はOFF。** LicenseGateと同じフェイルクローズ方針で、プラグイン導入直後に意図せず公開が
始まらないようにしている。許諾確認・動作確認が済んでから管理画面でONにすること。

## 手動再実行について

個別の日・開催場を再生成・再公開したい場合は、収集アプリ側（VPS上のKeibaDataCollector）で
`run-score.bat` / `run-content.bat`を対象日を指定して再実行する運用のままにしている。
このプラグインからVPS上の処理を起動する経路（Webhook等）は意図的に作っていない。
Webから任意のバッチ実行を引き起こせる経路を増やすと、その分だけ攻撃面が増えるため。

## インストール手順

1. このディレクトリ（`keiba-ai-digest`）を丸ごと`wp-content/plugins/`にコピー
2. WordPress管理画面 → プラグイン → 「Keiba AI Digest」を有効化
3. 設定 → パーマリンク設定 を開いて「保存」を押す（`ai-digest`の書き換えルールを反映させるため）
4. 設定 → Keiba AI Digest で許諾確認・動作確認後に自動公開をON

## データ形式の取り決め

`ai_index_top5` / `trend_morning` / `trend_live` / `trend_final` / `picks` は、`keiba-race-sync`の
`race_card`等と同じ理由（WordPress REST APIのメタスキーマ検証がネストした任意配列を安定して扱えない
ため）で、**camelCaseキーのJSON文字列**として保存する（`WordPressClient.cs`側もこの形式で送信する）。
列挙型（`picks[].category`＝`Nerai`/`Ana`/`Kiken`、`trend_*.stage`＝`Morning`/`Live`/`Final`）は
整数値ではなく列挙子名の文字列で送る（`WordPressClient.cs`の`CamelCaseSettings`に`StringEnumConverter`
を追加済み）。

## 表示側の既知の制約

- 天候・馬場状態コードはJV-Dataコード表の名称に変換して表示する。表に無いコードは推測せず
  「コード○」と表示する
- **`license_visible`は公開時点のLicenseGate判定のスナップショット**であり、表示のたびに許諾状態を
  再確認するものではない。許諾が後から取り消された場合、既に公開済みの投稿は次回の更新まで
  表示され続ける（`horse-race-custom-builder`の`hrc_is_race_visible`と同じ運用上の制約）
