using DietTime.Application;
using DietTime.Domain;

namespace DietTime.Persistence;

/// <summary>Plan media only. Purchased versions never inherit images from a future version.</summary>
public static class MealPlanImages
{
    public static IOrderedQueryable<MealMedia> ForPurchasedPlan(
        IQueryable<MealMedia> media, IQueryable<MealPlanTemplate> plans, MealPlanTemplate purchased) =>
        media.Where(image => image.Status == "ACTIVE" && image.MediaType == MealMediaTypes.MealPlan &&
            (image.EntityId == purchased.Id || purchased.VersionGroupId != Guid.Empty &&
                plans.Any(version => version.Id == image.EntityId &&
                    version.VersionGroupId == purchased.VersionGroupId &&
                    version.VersionNumber <= purchased.VersionNumber)))
            .OrderByDescending(image => image.EntityId == purchased.Id)
            .ThenByDescending(image => image.IsPrimary)
            .ThenBy(image => image.DisplayOrder)
            .ThenByDescending(image => image.UpdatedAt)
            .ThenBy(image => image.Id);

    // Shared with the pricing/purchase image mapping. Object keys use the configured CDN/S3 base.
    public static string? ResolveUrl(IStorageUrlService storage, string? thumbnailUrl,
        string? thumbnailObjectKey, string? publicUrl, string? objectKey)
    {
        static string? Absolute(string? value) =>
            Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri.AbsoluteUri : null;
        if (Absolute(thumbnailUrl) is { } thumbnail) return thumbnail;
        if (!string.IsNullOrWhiteSpace(thumbnailObjectKey)) return storage.GetThumbnailUrl(thumbnailObjectKey);
        if (Absolute(publicUrl) is { } image) return image;
        if (!string.IsNullOrWhiteSpace(objectKey)) return storage.GetPublicUrl(objectKey);
        return null;
    }
}
