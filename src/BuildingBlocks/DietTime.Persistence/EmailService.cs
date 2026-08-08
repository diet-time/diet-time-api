using System.Net;
using System.Net.Mail;
using System.Text;
using DietTime.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DietTime.Persistence;

public class SmtpOptions
{
    public const string SectionName = "Smtp";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "DietTime";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseSsl { get; set; } = true;
}

public class EmailService : IEmailService
{
    private readonly SmtpOptions _smtpOptions;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IOptions<SmtpOptions> smtpOptions, ILogger<EmailService> logger)
    {
        _smtpOptions = smtpOptions.Value;
        _logger = logger;
    }

    public async Task<bool> SendPasswordResetEmailAsync(string toEmail, string firstName, string resetUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            // Load email template
            var templatePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "EmailTemplates", "PasswordReset.html");
            if (!File.Exists(templatePath))
            {
                _logger.LogWarning("Password reset email template not found at {TemplatePath}", templatePath);
                return false;
            }

            var htmlContent = await File.ReadAllTextAsync(templatePath, cancellationToken);
            
            // Replace placeholders
            htmlContent = htmlContent
                .Replace("{FirstName}", firstName)
                .Replace("{ResetUrl}", resetUrl);

            // Send via SMTP
            using (var client = new SmtpClient(_smtpOptions.Host, _smtpOptions.Port))
            {
                client.EnableSsl = _smtpOptions.UseSsl;
                
                if (!string.IsNullOrWhiteSpace(_smtpOptions.Username) && !string.IsNullOrWhiteSpace(_smtpOptions.Password))
                {
                    client.Credentials = new NetworkCredential(_smtpOptions.Username, _smtpOptions.Password);
                }

                using (var message = new MailMessage())
                {
                    message.From = new MailAddress(_smtpOptions.FromEmail, _smtpOptions.FromName);
                    message.To.Add(new MailAddress(toEmail));
                    message.Subject = "Password Reset Request - DietTime";
                    message.Body = htmlContent;
                    message.IsBodyHtml = true;

                    await client.SendMailAsync(message, cancellationToken);
                }
            }

            _logger.LogInformation("Password reset email sent to {Email}", toEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send password reset email to {Email}", toEmail);
            return false;
        }
    }
}
