using System;
using System.Configuration;

namespace KeibaDataCollector
{
    // 機密情報は環境変数を優先。App.configの値は非機密の既定値/開発時フォールバックとしてのみ使う。
    // (keiba-race-result-auto-posting の AppConfig.cs と同じ方針。Phase 2でJV-Link/UmaConn関連の
    //  設定値を合流させる前提で、キー名もそちらに合わせてある。)
    internal static class AppConfig
    {
        public static string HistoricalDbPath => GetOrDefault("HistoricalDbPath", "data\\historical.sqlite3");

        private static string GetOrDefault(string key, string defaultValue)
        {
            var fromEnv = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

            var fromConfig = ConfigurationManager.AppSettings[key];
            if (!string.IsNullOrEmpty(fromConfig)) return fromConfig;

            return defaultValue;
        }
    }
}
