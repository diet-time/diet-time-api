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
    public async Task<IActionResult> Export(CancellationToken ct) => File(
        await new MasterDataTranslationExport(db).GenerateAsync(ct), "text/csv; charset=utf-8",
        $"diet-time-master-data-translations-{clock.GetUtcNow():yyyy-MM-dd}.csv");
}
