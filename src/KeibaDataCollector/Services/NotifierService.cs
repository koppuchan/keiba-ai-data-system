using System;
using System.Net.Mail;
using KeibaDataCollector.Data;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 仕様書§15エラー処理表の「通知」列に対応する。すべてのエラーはAuditLogStoreへ必ず記録する
    /// （＝監視ダッシュボードで確認できる）。それに加えて、severity=Criticalのものだけ
    /// メール通知を試みる（データ取得失敗の最終再試行失敗、WordPress API失敗の最終再試行失敗、
    /// LicenseGate未承認での公開強制停止など）。
    ///
    /// メール送信はベストエフォート。SMTP設定（AppConfig）が無ければ何もしない
    /// （このシステムを動かすのに外部メールサービスの契約を必須にしたくないため）。
    /// 送信自体に失敗しても例外を外へ投げない（通知の失敗でバッチ本体を止めたくないため）。
    /// </summary>
    public class NotifierService
    {
        private readonly AuditLogStore _auditLog;

        public const string SeverityInfo = "Info";
        public const string SeverityError = "Error";
        public const string SeverityCritical = "Critical";

        public NotifierService(AuditLogStore auditLog)
        {
            _auditLog = auditLog;
        }

        public void Notify(string severity, string source, string category, string message)
        {
            _auditLog.Log(severity, source, category, message);
            Console.WriteLine($"[{severity}] [{source}] [{category}] {message}");

            if (severity == SeverityCritical)
                TrySendEmail(source, category, message);
        }

        private void TrySendEmail(string source, string category, string message)
        {
            var host = AppConfig.SmtpHost;
            var to = AppConfig.SmtpTo;
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(to))
                return; // 未設定。ダッシュボード・ログでの確認のみに留める。

            try
            {
                using (var client = new SmtpClient(host, AppConfig.SmtpPort))
                {
                    if (!string.IsNullOrEmpty(AppConfig.SmtpUser))
                        client.Credentials = new System.Net.NetworkCredential(AppConfig.SmtpUser, AppConfig.SmtpPassword);
                    client.EnableSsl = AppConfig.SmtpUseSsl;

                    using (var mail = new MailMessage(AppConfig.SmtpFrom, to))
                    {
                        mail.Subject = $"[KeibaDataCollector][{category}] {source}";
                        mail.Body = message;
                        client.Send(mail);
                    }
                }
            }
            catch (Exception ex)
            {
                // 通知の失敗自体は監査ログにだけ残し、バッチ本体には影響させない。
                Console.WriteLine($"[NotifierService] メール通知に失敗しました: {ex.Message}");
            }
        }
    }
}
