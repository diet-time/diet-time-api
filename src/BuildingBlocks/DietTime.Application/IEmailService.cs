using System.Text;

namespace DietTime.Application;

public interface IEmailService
{
    Task<bool> SendPasswordResetEmailAsync(string toEmail, string firstName, string resetUrl, CancellationToken cancellationToken = default);
}
