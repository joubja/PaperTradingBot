using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using PaperTradingBot.Config;

namespace PaperTradingBot.Services;

public class NotificationService
{
    private readonly NotificationOptions _opts;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        IOptions<NotificationOptions> opts,
        ILogger<NotificationService> logger)
    {
        _opts   = opts.Value;
        _logger = logger;
    }

    public async Task SendAsync(string subject, string body)
    {
        if (!_opts.Enabled || string.IsNullOrWhiteSpace(_opts.EmailAppPassword)) return;
        await SendEmailAsync(subject, body);
    }

    private async Task SendEmailAsync(string subject, string body)
    {
        try
        {
            var msg = new MimeMessage();
            msg.From.Add(MailboxAddress.Parse(_opts.EmailFrom));
            msg.To.Add(MailboxAddress.Parse(_opts.EmailTo));
            msg.Subject = $"[PaperBot] {subject}";
            msg.Body    = new TextPart("plain") { Text = body };

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_opts.EmailSmtpHost, _opts.EmailSmtpPort, SecureSocketOptions.StartTls);
            await smtp.AuthenticateAsync(_opts.EmailFrom, _opts.EmailAppPassword);
            await smtp.SendAsync(msg);
            await smtp.DisconnectAsync(true);

            _logger.LogInformation("NOTIFICATION | Email sent: {Subject}", subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NOTIFICATION | Email failed: {Subject}", subject);
        }
    }
}
