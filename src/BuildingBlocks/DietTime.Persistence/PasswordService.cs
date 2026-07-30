using System.Security.Cryptography;
using System.Text;
using DietTime.Application;
using DietTime.Domain;

namespace DietTime.Persistence;

public class PasswordService : IPasswordService
{
    private readonly DietTimeDbContext _context;
    private const int PasswordResetTokenExpiryHours = 24;
    private const string PasswordKey = "PWD";
    private const string PasswordResetTokenKey = "PWDGEN";

    public PasswordService(DietTimeDbContext context)
    {
        _context = context;
    }

    public string HashPassword(string password)
    {
        using (var sha256 = SHA256.Create())
        {
            var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(hashedBytes);
        }
    }

    public bool VerifyPassword(string password, string hash)
    {
        var hashOfInput = HashPassword(password);
        return hashOfInput.Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken)
    {
        var hashedPassword = HashPassword(password);
        var now = DateTimeOffset.UtcNow;

        // Check if user has existing password record
        var existingPassword = _context.UserAttributes.FirstOrDefault(x => x.UserId == userId && x.Key == PasswordKey);
        if (existingPassword != null)
        {
            existingPassword.Value = hashedPassword;
            existingPassword.UpdatedAt = now;
        }
        else
        {
            var userAttribute = new UserAttribute
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Key = PasswordKey,
                Value = hashedPassword,
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.UserAttributes.Add(userAttribute);
        }

        // Clear reset token
        var resetToken = _context.UserAttributes.FirstOrDefault(x => x.UserId == userId && x.Key == PasswordResetTokenKey);
        if (resetToken != null)
        {
            _context.UserAttributes.Remove(resetToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> VerifyPasswordAsync(Guid userId, string password)
    {
        var passwordAttribute = _context.UserAttributes.FirstOrDefault(x => x.UserId == userId && x.Key == PasswordKey);
        if (passwordAttribute == null)
            return false;

        return VerifyPassword(password, passwordAttribute.Value);
    }

    public async Task<string> GeneratePasswordResetTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        var resetToken = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        // Check if reset token already exists
        var existingToken = _context.UserAttributes.FirstOrDefault(x => x.UserId == userId && x.Key == PasswordResetTokenKey);
        if (existingToken != null)
        {
            existingToken.Value = resetToken;
            existingToken.UpdatedAt = now;
        }
        else
        {
            var tokenAttribute = new UserAttribute
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Key = PasswordResetTokenKey,
                Value = resetToken,
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.UserAttributes.Add(tokenAttribute);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return resetToken;
    }

    public async Task<bool> ValidatePasswordResetTokenAsync(Guid userId, string token, CancellationToken cancellationToken)
    {
        var tokenAttribute = _context.UserAttributes.FirstOrDefault(x => x.UserId == userId && x.Key == PasswordResetTokenKey);
        if (tokenAttribute == null)
            return false;

        // Check if token matches
        if (!tokenAttribute.Value.Equals(token, StringComparison.OrdinalIgnoreCase))
            return false;

        // Check if token has expired (24 hours)
        var tokenAge = DateTimeOffset.UtcNow - tokenAttribute.UpdatedAt;
        if (tokenAge.TotalHours > PasswordResetTokenExpiryHours)
            return false;

        return true;
    }

    public async Task<bool> SetPasswordWithResetTokenAsync(Guid userId, string resetToken, string newPassword, CancellationToken cancellationToken)
    {
        // Validate reset token first
        if (!await ValidatePasswordResetTokenAsync(userId, resetToken, cancellationToken))
            return false;

        // Set new password (which also clears the reset token)
        return await SetPasswordAsync(userId, newPassword, cancellationToken);
    }
}
