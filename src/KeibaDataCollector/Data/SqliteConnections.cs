using System;
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

            // 待ち時間は接続の確立時点から効かせる。以前はPRAGMA journal_mode=WALを先に実行して
            // いたため、複数のタスクが同時に起動した瞬間に他方がDBを掴んでいると、待たずに即
            // "database is locked" となり、起動自体が失敗した（実機: deploy直後に4タスクが同時起動）。
            var conn = new SQLiteConnection($"Data Source={dbPath};Version=3;BusyTimeout=10000;");
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA busy_timeout=10000;";
                cmd.ExecuteNonQuery();
            }

            // WALはDBファイルに永続化される設定のため、すでにWALなら変更しない
            // （変更しようとするだけで排他的なロックが必要になり、他のタスクと衝突しやすい）。
            string mode;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode;";
                mode = Convert.ToString(cmd.ExecuteScalar());
            }
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA journal_mode=WAL;";
                    cmd.ExecuteNonQuery();
                }
            }

            return conn;
        }
    }
}
