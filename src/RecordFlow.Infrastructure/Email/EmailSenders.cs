using System.Collections.Concurrent;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using RecordFlow.Core.Abstractions;

namespace RecordFlow.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>"Smtp" for real delivery, "Development" to capture messages in the in-app dev mailbox.</summary>
    public string Provider { get; set; } = "Development";
    public string FromAddress { get; set; } = "no-reply@example.com";
    public string FromName { get; set; } = "RecordFlow";
    public SmtpSettings Smtp { get; set; } = new();
}

public sealed class SmtpSettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IAppEmailSender
{
    public async Task SendAsync(string toAddress, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        var o = options.Value;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = textBody }.ToMessageBody();

        if (string.IsNullOrWhiteSpace(o.Smtp.Host)) throw new InvalidOperationException("Email:Smtp:Host is not configured.");

        using var client = new SmtpClient();
        await client.ConnectAsync(o.Smtp.Host, o.Smtp.Port, o.Smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, ct);
        if (!string.IsNullOrEmpty(o.Smtp.UserName))
            await client.AuthenticateAsync(o.Smtp.UserName, o.Smtp.Password ?? string.Empty, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
        logger.LogInformation("Email '{Subject}' sent.", subject);
    }
}

/// <summary>Development-only sender: keeps the last messages in memory for the /dev/mailbox page.</summary>
public sealed class DevelopmentEmailSender(DevMailbox mailbox, ILogger<DevelopmentEmailSender> logger) : IAppEmailSender
{
    public Task SendAsync(string toAddress, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        mailbox.Add(new DevMailMessage(Guid.NewGuid(), DateTimeOffset.UtcNow, toAddress, subject, htmlBody, textBody));
        logger.LogInformation("[DEV EMAIL] To: {To} | Subject: {Subject} – open /dev/mailbox to read it.", toAddress, subject);
        return Task.CompletedTask;
    }
}

public sealed record DevMailMessage(Guid Id, DateTimeOffset SentAtUtc, string To, string Subject, string HtmlBody, string TextBody);

public sealed class DevMailbox
{
    private readonly ConcurrentQueue<DevMailMessage> _messages = new();

    public void Add(DevMailMessage message)
    {
        _messages.Enqueue(message);
        while (_messages.Count > 50 && _messages.TryDequeue(out _)) { }
    }

    public IReadOnlyList<DevMailMessage> All() => _messages.Reverse().ToList();
}
