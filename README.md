# keiba-ai-data-system

JRAVAN＋競馬最強の法則WEB 中央・地方競馬 全自動AI競馬データシステム。既存の稼働中システム
（`keiba-race-result-auto-posting` / `horse-race-custom-builder`）を土台に、AI指数算出・傾向分析・
コンテンツ自動生成・LicenseGate・監視ダッシュボードを追加したWindows常駐アプリ＋WordPress連携。

## 構成

- [`src/KeibaDataCollector/`](src/KeibaDataCollector/) — 収集アプリ本体（.NET Framework 4.8 / x86）
- [`src/wordpress-plugin/keiba-ai-digest/`](src/wordpress-plugin/keiba-ai-digest/) — WordPress companion プラグイン

## 実行手順（概要）

1. Windows VPS上で `dotnet build -c Debug`
2. `secrets.local.bat.example` を `secrets.local.bat` にコピーし、WordPress・JV-Link/UmaConnの認証情報を設定
3. `KeibaDataCollector.exe setup` を実行し、JV-Link/UmaConnの利用キーを登録
4. WordPress側に `keiba-ai-digest` プラグインを導入
5. `register-scheduled-tasks.ps1` でTask Schedulerに全自動更新タスクを登録

コマンド一覧・詳細な手順・運用上の注意点は [`src/KeibaDataCollector/README.md`](src/KeibaDataCollector/README.md) を参照。

## 前提

JRA-VAN・競馬最強の法則WEB双方からの公開・商用利用許諾が確認できるまで、WordPressへの自動公開は
設計上ブロックされる（LicenseGateがフェイルクローズで動作する）。許諾確認はお客様側の手続きに依存する。
