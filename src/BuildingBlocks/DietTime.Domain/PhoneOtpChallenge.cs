namespace DietTime.Domain;

// Abuse-protection metadata only. No generated code, test code, or OTP hash is stored.
public sealed class PhoneOtpChallenge
{
    public string PhoneNumber { get; set; } = "";
    public string Mode { get; set; } = "";
    public string? ProviderRequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset NextSendAt { get; set; }
    public DateTimeOffset SendWindowStartedAt { get; set; }
    public int SendCount { get; set; }
    public int VerificationAttempts { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
