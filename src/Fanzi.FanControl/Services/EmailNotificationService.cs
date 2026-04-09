using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

public sealed class EmailNotificationService
{
    public async Task<string> SendAsync(
        string smtpHost,
        int smtpPort,
        string smtpUser,
        string smtpPassword,
        string toEmail,
        string subject,
        string body)
    {
        try
        {
            using SmtpClient client = new(smtpHost, smtpPort)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(smtpUser, smtpPassword),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 15_000,
            };

            using MailMessage message = new(smtpUser, toEmail, subject, body);
            await client.SendMailAsync(message);
            return $"\u2713  Email sent to {toEmail}.";
        }
        catch (SmtpException ex)
        {
            return $"SMTP error: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Failed to send: {ex.Message}";
        }
    }
}
