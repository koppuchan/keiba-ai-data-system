# keiba-ai-data-system

JRAVAN＋競馬最強の法則WEB 中央・地方競馬 全自動AI競馬データシステム。

既存の稼働中システム（[`keiba-race-result-auto-posting`](https://github.com/koppuchan/keiba-race-result-auto-posting)、
[`horse-race-custom-builder`](https://github.com/koppuchan/horse-race-custom-builder)）を土台に、
LicenseGate・傾向分析・コンテンツ自動生成・Validator・監視ダッシュボードを追加していくプロジェクト。

- 収集アプリ本体: [`src/KeibaDataCollector/`](src/KeibaDataCollector/)
  （コマンド一覧・VPSへの初回デプロイ手順・Task Scheduler登録・運用手順は同ディレクトリのREADME参照）
- WordPress companion プラグイン: [`src/wordpress-plugin/keiba-ai-digest/`](src/wordpress-plugin/keiba-ai-digest/)
  （AI指数TOP5・本日の傾向・狙い馬/穴馬/危険な人気馬。既存の`keiba-race-sync`とは別プラグイン）

## 着手前の前提

仕様書§5・§22の通り、JRA-VAN・競馬最強の法則WEB双方からの公開・商用利用許諾が確認できるまで、
WordPressへの自動公開は設計上ブロックされる（LicenseGateがフェイルクローズで動作する）。
許諾状況の確認はお客様側の手続きに依存するため、実装完了後もこの確認が別途必要。
