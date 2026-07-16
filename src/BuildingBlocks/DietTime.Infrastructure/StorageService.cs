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
    public string Region { get; set; } = "auto";
    public bool ForcePathStyle { get; set; } = true;
    public long MaxUploadSizeBytes { get; set; } = 10 * 1024 * 1024;
}

public sealed class StorageUrlService(IOptions<StorageOptions> options, IAmazonS3 s3) : IStorageUrlService
{
    private readonly StorageOptions settings = options.Value;
    public long MaxUploadSizeBytes => settings.MaxUploadSizeBytes;
    public string GetPublicUrl(string objectKey) => Build(objectKey);
    public string GetThumbnailUrl(string objectKey) => Build(objectKey);

    public async Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken ct)
    {
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.BucketName,
            Key = objectKey,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false
        }, ct);
    }

    public Task DeleteAsync(string objectKey, CancellationToken ct) =>
        s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = settings.BucketName, Key = objectKey }, ct);

    private string Build(string key) => $"{settings.PublicBaseUrl.TrimEnd('/')}/{string.Join('/', key.Split('/').Select(Uri.EscapeDataString))}";
}
