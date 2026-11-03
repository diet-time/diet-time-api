using DietTime.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Dietitian,ContentManager")]
[Route("api/admin/master-data/export")]
[Route("api/v1/admin/master-data/export")]
public sealed class MasterDataExportController(DietTimeDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Export([FromQuery] string? resource, CancellationToken ct, [FromQuery] Guid? planId = null)
    {
        if (!MasterDataTranslationExport.Supports(resource) || planId.HasValue && resource != "meal-plans")
            return BadRequest(new ProblemDetails { Detail = "Choose the master-data screen to export." });
        return File(await new MasterDataTranslationExport(db).GenerateAsync(resource!, ct, planId),
            "text/csv; charset=utf-8", $"diet-time-{resource}-translations-{clock.GetUtcNow():yyyy-MM-dd}.csv");
    }
}
