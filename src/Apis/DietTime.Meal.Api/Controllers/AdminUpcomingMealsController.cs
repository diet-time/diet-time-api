using Asp.Versioning;
using DietTime.Application;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.Controllers;

[ApiController, ApiVersion(1), Authorize(Roles = "Admin,Operations")]
[Route("api/v{version:apiVersion}/admin/orders/{orderId:guid}/initialize-upcoming-meals")]
public sealed class AdminUpcomingMealsController(DietTimeDbContext db, IUpcomingMealService meals, TimeProvider clock) : ControllerBase
{
    // Explicit operational backfill for existing active orders. GET requests never
    // create assignments, and historical/legacy subscription records stay intact.
    [HttpPost]
    public async Task<IActionResult> Initialize(Guid orderId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddHours(3));
        var order = await db.Orders.Include(o => o.Meals).Include(o => o.DeliveryDays)
            .SingleOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return NotFound();
        if (order.Status is not ("CONFIRMED" or "ACTIVE") || order.EndDate < today)
            return Conflict(new ProblemDetails { Status = 409, Title = "Only active orders with upcoming deliveries can be initialized." });
        if (order.DeliveryDays.Any(d => d.DayOfWeek == 5))
            return Conflict(new ProblemDetails { Status = 409, Title = "This legacy order includes Friday. Operations must review its delivery schedule before initialization." });
        await meals.InitializeAsync(order, ct);
        return Ok(new { serviceDays = await db.Set<SubscriptionServiceDay>().CountAsync(d => d.OrderId == orderId, ct) });
    }
}
