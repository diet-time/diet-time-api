using Asp.Versioning;
using DietTime.Contracts;
using DietTime.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/takeaway-branches")]
[Authorize]
public sealed class TakeawayBranchesController(DietTimeDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) => Ok(new
    {
        items = await db.TakeawayBranches.AsNoTracking().Where(x => x.IsActive)
            .OrderBy(x => x.Name).Select(x => new TakeawayBranchResponse(x.Id, x.Name, x.Address))
            .ToArrayAsync(cancellationToken)
    });
}
