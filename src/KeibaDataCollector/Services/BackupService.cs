using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using KeibaDataCollector.Data;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 仕様書§12「深夜: バックアップ」。historical.sqlite3（予測snapshot・検証結果・LicenseGate等を
    /// 含む）の一貫したコピーを作り、直近の世代だけ残す。稼働中のDBをファイルコピーすると
    /// WALの書き込み途中で壊れたコピーになりうるため、SQLiteのバックアップAPIを使う。
    /// </summary>
    public static class BackupService
    {
        public static string Run(string dbPath, int keep)
        {
            var backupDir = Path.Combine(Path.GetDirectoryName(dbPath), "backup");
            Directory.CreateDirectory(backupDir);

            var dest = Path.Combine(backupDir, $"historical-{DateTime.Now:yyyyMMdd-HHmmss}.sqlite3");
            using (var source = SqliteConnections.Open(dbPath))
            using (var target = new SQLiteConnection($"Data Source={dest};Version=3;"))
            {
                target.Open();
                source.BackupDatabase(target, "main", "main", -1, null, 0);
            }

            var stale = Directory.GetFiles(backupDir, "historical-*.sqlite3")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(keep);
            foreach (var file in stale)
                File.Delete(file);

            return dest;
        }
    }
}
