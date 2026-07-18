using Asp.Versioning;
using DietTime.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController, ApiVersion(1), AllowAnonymous, Route("api/v{version:apiVersion}/media")]
public sealed class MediaController(IStorageUrlService storage) : ControllerBase
{
    [HttpGet("{**objectKey}")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Get(string objectKey, CancellationToken ct)
    {
        if (!IsValidMealObjectKey(objectKey))
            return NotFound();

        var storedObject = await storage.DownloadAsync(objectKey, ct);
        if (storedObject is null)
            return NotFound();

        if (storedObject.Length is > 0)
            Response.ContentLength = storedObject.Length;
        return File(storedObject.Content, storedObject.ContentType, enableRangeProcessing: false);
    }

    private static bool IsValidMealObjectKey(string objectKey)
    {
        if (!objectKey.StartsWith("meals/", StringComparison.Ordinal))
            return false;

        var segments = objectKey.Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not "." and not "..");
    }
}
