using Amazon.S3;
using Amazon.S3.Model;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.Extensions.Options;

namespace DietTime.Infrastructure;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public string? ServiceUrl { get; set; }
    public string PublicBaseUrl { get; set; } = ""; public string BucketName { get; set; } = ""; public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string Region { get; set; } = "auto"; public int UploadExpiryMinutes { get; set; } = 10;
    public bool ForcePathStyle { get; set; } = true;
}

public sealed class StorageUrlService(IOptions<StorageOptions> options, IAmazonS3 s3, TimeProvider clock) : IStorageUrlService
{
    private readonly StorageOptions settings = options.Value;
    public string GetPublicUrl(string objectKey) => Build(objectKey);
    public string GetThumbnailUrl(string objectKey) => Build(objectKey);
    public Task<UploadUrlResponse> CreateUploadUrlAsync(UploadUrlRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var ext = Path.GetExtension(request.FileName).ToLowerInvariant(); var key = $"meals/{request.EntityId:D}/{Guid.NewGuid():N}{ext}"; var expires = clock.GetUtcNow().AddMinutes(settings.UploadExpiryMinutes);
        var uploadUrl = s3.GetPreSignedURL(new GetPreSignedUrlRequest { BucketName = settings.BucketName, Key = key, Verb = HttpVerb.PUT, ContentType = request.ContentType, Expires = expires.UtcDateTime });
        return Task.FromResult(new UploadUrlResponse(uploadUrl, key, expires));
    }
    private string Build(string key) => $"{settings.PublicBaseUrl.TrimEnd('/')}/{string.Join('/', key.Split('/').Select(Uri.EscapeDataString))}";
}
