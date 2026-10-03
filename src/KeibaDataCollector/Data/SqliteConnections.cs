using System.Data.SQLite;
using System.IO;

namespace KeibaDataCollector.Data
{
    /// <summary>
    /// 各Storeクラス（HistoricalDataStore/ScoresStore/PredictionStore等）が個別に
    /// dbPathからSQLiteConnectionを開く際の共通処理。
    ///
    /// 既定のrollbackジャーナルモードはDB全体を書き込みのたびに排他ロックするため、
    /// score/content/trend/verify/dashboard等の複数タスクがTask Schedulerで
    /// 重なって実行されると、他方が一瞬書き込み中なだけで即座に
    /// "database is locked" (SQLITE_BUSY) 例外になる（既定のbusy timeoutが0のため、
    /// 実機のcontentコマンドで実際に発生）。WALモード＋busy_timeoutで、
    /// 短い重なりなら例外にならず待ち合わせて成功するようにする。
    /// </summary>
    internal static class SqliteConnections
    {
        public static SQLiteConnection Open(string dbPath)
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var conn = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            conn.Open();

            // 1コマンドに複数PRAGMAをまとめると一部しか実行されない場合があるため、個別に実行する。
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL;";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA busy_timeout=10000;";
                cmd.ExecuteNonQuery();
            }

            return conn;
        }
    }
}
