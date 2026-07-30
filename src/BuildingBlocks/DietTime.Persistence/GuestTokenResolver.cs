using DietTime.Application;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class GuestTokenResolver(
    DietTimeDbContext db,
    IGuestTokenHasher hasher,
    TimeProvider clock) : IGuestTokenResolver
{
    public async Task<GuestTokenResolution> ResolveAsync(
        IReadOnlyList<string> tokenHeaders,
        bool requireProfile,
        CancellationToken ct)
    {
        if (tokenHeaders.Count != 1 ||
            !hasher.IsValidFormat(tokenHeaders[0]))
        {
            return Invalid();
        }

        var rawToken = tokenHeaders[0];
        var tokenHash = hasher.Hash(rawToken);
        var profile = await db.CustomerProfiles.AsNoTracking()
            .Where(x => x.GuestTokenHash == tokenHash)
            .Select(x => new
            {
                x.Id,
                x.UserId,
                x.GuestTokenHash,
                x.GuestTokenExpiresAt,
                x.IsActive
            })
            .SingleOrDefaultAsync(ct);

        if (profile is null)
        {
            return requireProfile
                ? new(GuestTokenResolutionStatus.ProfileNotFound, tokenHash, null)
                : new(GuestTokenResolutionStatus.Valid, tokenHash, null);
        }

        if (profile.UserId is not null ||
            !profile.IsActive ||
            profile.GuestTokenExpiresAt is null ||
            profile.GuestTokenExpiresAt <= clock.GetUtcNow() ||
            profile.GuestTokenHash is null ||
            !hasher.Verify(rawToken, profile.GuestTokenHash))
        {
            return Invalid();
        }

        return new(GuestTokenResolutionStatus.Valid, tokenHash, profile.Id);
    }

    private static GuestTokenResolution Invalid() =>
        new(GuestTokenResolutionStatus.Invalid, null, null);
}
