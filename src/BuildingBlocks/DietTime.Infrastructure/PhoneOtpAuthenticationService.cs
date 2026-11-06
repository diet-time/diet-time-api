using DietTime.Application;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace DietTime.Infrastructure;

public sealed class PhoneOtpAuthenticationService(DietTimeDbContext db, IPhoneOtpProvider provider,
    OtpAuthenticationOptions options, TimeProvider clock) : IPhoneOtpAuthenticationService
{
    private string Mode => options.IsTestAuthentication ? "TEST" : "LIVE";
    private bool Allowed(string phone) => !options.IsTestAuthentication || options.AllowedTestNumbers.Contains(phone);

    public async Task<PhoneOtpSendResult> SendAsync(string phoneNumber, CancellationToken ct)
    {
        if (!PhoneOtpNumbers.TryNormalize(phoneNumber, out var phone) || !Allowed(phone)) return new(400);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serializes both first-time inserts and existing challenges across API replicas.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({phone}, 42761))", ct);
        var now = clock.GetUtcNow();
        var challenge = await db.Set<PhoneOtpChallenge>().SingleOrDefaultAsync(c => c.PhoneNumber == phone, ct);
        if (challenge != null) await db.Entry(challenge).ReloadAsync(ct);
        if (challenge != null && challenge.NextSendAt > now)
            return new(429, RetryAfterSeconds: Math.Max(1, (int)Math.Ceiling((challenge.NextSendAt - now).TotalSeconds)));
        if (challenge != null && challenge.SendWindowStartedAt.AddMinutes(15) > now && challenge.SendCount >= 5)
            return new(429, RetryAfterSeconds: Math.Max(1, (int)Math.Ceiling((challenge.SendWindowStartedAt.AddMinutes(15) - now).TotalSeconds)));
        if (challenge != null && challenge.Mode == Mode && challenge.ExpiresAt > now && challenge.VerificationAttempts >= OtpAuthenticationOptions.MaximumAttempts)
            return new(429, RetryAfterSeconds: Math.Max(1, (int)Math.Ceiling((challenge.ExpiresAt - now).TotalSeconds)));

        if (challenge == null) {
            challenge = new() { PhoneNumber = phone, Mode = Mode, CreatedAt = now, ExpiresAt = now, SendWindowStartedAt = now };
            db.Add(challenge);
        }
        if (challenge.SendWindowStartedAt.AddMinutes(15) <= now) { challenge.SendWindowStartedAt = now; challenge.SendCount = 0; }
        challenge.NextSendAt = now.AddSeconds(OtpAuthenticationOptions.ResendSeconds);
        challenge.SendCount++;
        // An expired/live session must be cancelled before issuing a fresh code, because Verify
        // otherwise resends the old token during its default ten-minute lifetime.
        if (challenge.Mode == "LIVE" && challenge.ProviderRequestId != null &&
            (challenge.ExpiresAt <= now || challenge.ConsumedAt != null || challenge.Mode != Mode))
        {
            if (!await provider.CancelAsync(challenge.ProviderRequestId, ct)) {
                challenge.ConsumedAt ??= now;
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                return new(503);
            }
        }
        var sent = await provider.SendAsync(phone, ct);
        if (sent.StatusCode != 200 || string.IsNullOrEmpty(sent.RequestId)) {
            // A send failure (including an ambiguous timeout) invalidates the local challenge.
            challenge.ConsumedAt ??= now;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return sent.StatusCode == 200 ? new(503) : sent;
        }
        var sameActiveChallenge = challenge.Mode == Mode && challenge.ProviderRequestId == sent.RequestId &&
                                  challenge.ExpiresAt > now && challenge.ConsumedAt == null;
        if (!sameActiveChallenge) {
            challenge.CreatedAt = now;
            challenge.ExpiresAt = now.AddSeconds(OtpAuthenticationOptions.LifetimeSeconds);
            challenge.VerificationAttempts = 0;
            challenge.ConsumedAt = null;
        }
        challenge.Mode = Mode;
        challenge.ProviderRequestId = sent.RequestId;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return sent with { ExpiresInSeconds = Math.Max(1, (int)(challenge.ExpiresAt - now).TotalSeconds), ResendAfterSeconds = OtpAuthenticationOptions.ResendSeconds };
    }

    public async Task<PhoneOtpVerificationStatus> VerifyAsync(string phoneNumber, string otp, CancellationToken ct)
    {
        if (!PhoneOtpNumbers.TryNormalize(phoneNumber, out var phone) || !Allowed(phone) ||
            !Regex.IsMatch(otp ?? "", @"\A[0-9]{6}\z")) return PhoneOtpVerificationStatus.Invalid;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({phone}, 42761))", ct);
        var now = clock.GetUtcNow();
        // Reload rather than trusting a context identity-map entry from a previous call.
        var challenge = await db.Set<PhoneOtpChallenge>().SingleOrDefaultAsync(c => c.PhoneNumber == phone, ct);
        if (challenge != null) await db.Entry(challenge).ReloadAsync(ct);
        if (challenge == null || challenge.Mode != Mode || challenge.ProviderRequestId == null ||
            challenge.ConsumedAt != null || challenge.ExpiresAt <= now) return PhoneOtpVerificationStatus.Invalid;
        if (challenge.VerificationAttempts >= OtpAuthenticationOptions.MaximumAttempts) return PhoneOtpVerificationStatus.TooManyAttempts;
        challenge.VerificationAttempts++;
        // Persist the attempt before the network call; no provider retry is performed here.
        await db.SaveChangesAsync(ct);
        var verified = await provider.VerifyAsync(phone, otp!, ct);
        var finished = clock.GetUtcNow();
        if (verified == PhoneOtpVerificationStatus.Valid) {
            challenge.ConsumedAt = finished;
            if (finished >= challenge.ExpiresAt) verified = PhoneOtpVerificationStatus.Invalid;
        }
        if (verified == PhoneOtpVerificationStatus.Disabled) challenge.ConsumedAt ??= finished;
        await db.SaveChangesAsync(CancellationToken.None); await tx.CommitAsync(CancellationToken.None);
        return verified;
    }
}
