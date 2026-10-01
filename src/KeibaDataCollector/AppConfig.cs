using System;
using System.Configuration;

namespace KeibaDataCollector
{
    // 機密情報は環境変数を優先。App.configの値は非機密の既定値/開発時フォールバックとしてのみ使う。
    internal static class AppConfig
    {
        public static string JvLinkProgId => Get("JvLinkProgId");
        public static string UmaConnProgId => Get("UmaConnProgId");
        public static string JvLinkSoftwareId => Get("JvLinkSoftwareId");

        public static string WordPressBaseUrl => Get("WordPressBaseUrl");
        public static string WordPressUser => Get("WordPressUser");
        public static string WordPressAppPassword => Get("WordPressAppPassword");

        public static TimeSpan RealtimePollInterval =>
            TimeSpan.FromSeconds(int.Parse(Get("RealtimePollIntervalSeconds")));

        // license_gate を含む全テーブルを保持するSQLite。秘匿情報ではないため未設定でも起動できるよう既定値を持つ。
        //
        // 既定値・設定値とも相対パスの場合はexeの場所（AppDomain.CurrentDomain.BaseDirectory）を基準に
        // 解決する。カレントディレクトリ基準のままだと、bin\Debug\net48へcdしてから実行する
        // *.bat経由の実行と、別ディレクトリ（例: リポジトリ直下）から直接exeを呼んだ場合とで
        // 別々のSQLiteファイルを指してしまう（実機で発生: 直接呼んだlicensegateコマンドが
        // bat側からは見えない別DBへ書き込まれ、設定したはずの許諾状態が反映されなかった）。
        public static string HistoricalDbPath =>
            ResolveFromExeDirectory(GetOrDefault("HistoricalDbPath", "data\\historical.sqlite3"));

        private static string ResolveFromExeDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || System.IO.Path.IsPathRooted(path))
                return path;
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }

        // 仕様書§15「通知」用のSMTP設定。すべて任意（NotifierServiceは未設定なら送信自体を行わない）。
        public static string SmtpHost => GetOrDefault("SmtpHost", null);
        public static int SmtpPort => int.Parse(GetOrDefault("SmtpPort", "587"));
        public static bool SmtpUseSsl => bool.Parse(GetOrDefault("SmtpUseSsl", "true"));
        public static string SmtpUser => GetOrDefault("SmtpUser", null);
        public static string SmtpPassword => GetOrDefault("SmtpPassword", null);
        public static string SmtpFrom => GetOrDefault("SmtpFrom", null);
        public static string SmtpTo => GetOrDefault("SmtpTo", null);

        private static string Get(string key)
        {
            var value = GetOrDefault(key, null);
            if (value != null) return value;

            throw new InvalidOperationException(
                $"設定値 '{key}' が未設定です。環境変数か App.config に設定してください。");
        }

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
