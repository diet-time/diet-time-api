namespace DietTime.Persistence;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "DietTime.Api";
    public string Audience { get; set; } = "DietTime.App";
    public string Key { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
}
