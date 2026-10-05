using System;
using KeibaDataCollector.Data;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 仕様書§15エラー処理表の「通知」列に対応する。すべてのエラーはAuditLogStoreへ必ず記録する
    /// （＝監視ダッシュボードで確認できる）。それに加えて、severity=Criticalのものだけ
    /// メール通知を試みる（データ取得・AI指数生成・WordPress公開の失敗、更新停止の検知など）。
    ///
    /// メール送信はベストエフォート。SMTP設定（AppConfig）が無ければ何もしない
    /// （このシステムを動かすのに外部メールサービスの契約を必須にしたくないため）。
    /// 送信自体に失敗しても例外を外へ投げない（通知の失敗でバッチ本体を止めたくないため）。
    ///
    /// 同じ原因のメールを短時間に何通も送らない: タスクは20分おきに動くため、障害が続くと
    /// 同じ内容が延々と届く。同じ(source, category)には、直近の送信から一定時間
    /// （既定60分）は再送しない。
    /// </summary>
    public class NotifierService
    {
        private readonly AuditLogStore _auditLog;

        public const string SeverityInfo = "Info";
        public const string SeverityError = "Error";
        public const string SeverityCritical = "Critical";

        private const string MailSentCategory = "通知メール送信";
        private static readonly TimeSpan DefaultSuppressWindow = TimeSpan.FromMinutes(60);

        public NotifierService(AuditLogStore auditLog)
        {
            _auditLog = auditLog;
        }

        public void Notify(string severity, string source, string category, string message,
            TimeSpan? suppressWindow = null)
        {
            _auditLog.Log(severity, source, category, message);
            Console.WriteLine($"[{severity}] [{source}] [{category}] {message}");

            if (severity == SeverityCritical)
                TrySendEmail(source, category, message, suppressWindow ?? DefaultSuppressWindow);
        }

        /// <summary>SMTP設定の確認用。設定を使って1通送り、失敗した理由を返す（成功ならnull）。</summary>
        public static string SendTestMail()
        {
            if (!IsConfigured())
                return "SMTP設定（SmtpHost / SmtpTo）が未設定です。";
            try
            {
                Send("[KeibaDataCollector][テスト] メール通知の確認",
                    "このメールが届いていれば、KeibaDataCollectorのエラー通知メールは正しく設定されています。\n" +
                    $"送信時刻: {DateTime.Now:yyyy/MM/dd HH:mm:ss}");
                return null;
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        private static bool IsConfigured() =>
            !string.IsNullOrEmpty(AppConfig.SmtpHost) && !string.IsNullOrEmpty(AppConfig.SmtpTo);

        private void TrySendEmail(string source, string category, string message, TimeSpan suppressWindow)
        {
            if (!IsConfigured())
                return; // 未設定。ダッシュボード・ログでの確認のみに留める。

            var key = $"{source}|{category}";
            try
            {
                var last = _auditLog.LastInfoUtc(MailSentCategory, key);
                if (last.HasValue && DateTime.UtcNow - last.Value < suppressWindow)
                {
                    Console.WriteLine($"[NotifierService] 同じ内容のメールを{(int)suppressWindow.TotalMinutes}分以内に送信済みのため、再送を見送りました: {key}");
                    return;
                }

                Send($"[KeibaDataCollector][{category}] {source}", message);
                _auditLog.Log(SeverityInfo, source, MailSentCategory, key);
            }
            catch (Exception ex)
            {
                // 通知の失敗自体は画面・ログにだけ残し、バッチ本体には影響させない。
                Console.WriteLine($"[NotifierService] メール通知に失敗しました: {ex.Message}");
            }
        }

        private static void Send(string subject, string body)
        {
            var message = new MimeMessage();
            var from = string.IsNullOrEmpty(AppConfig.SmtpFrom) ? AppConfig.SmtpUser : AppConfig.SmtpFrom;
            message.From.Add(MailboxAddress.Parse(from));
            foreach (var address in AppConfig.SmtpTo.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                message.To.Add(MailboxAddress.Parse(address.Trim()));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };

            // 465は接続と同時にTLS（暗黙のTLS）。標準のSystem.Net.Mail.SmtpClientはこの方式に
            // 対応しないため、MailKitを使っている。587等はSTARTTLS。
            var security = AppConfig.SmtpPort == 465
                ? SecureSocketOptions.SslOnConnect
                : AppConfig.SmtpUseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None;

            using (var client = new SmtpClient())
            {
                client.Timeout = 30000;
                client.Connect(AppConfig.SmtpHost, AppConfig.SmtpPort, security);
                if (!string.IsNullOrEmpty(AppConfig.SmtpUser))
                    client.Authenticate(AppConfig.SmtpUser, AppConfig.SmtpPassword);
                client.Send(message);
                client.Disconnect(true);
            }
        }
    }
}
