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
    [HttpGet]
    public async Task<IActionResult> Preview(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Meals).Include(o => o.DeliveryDays).SingleOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return NotFound();
        var today = SubscriptionMenuRules.QatarToday(clock.GetUtcNow());
        var template = await db.MealPlanTemplates.AsNoTracking().SingleAsync(t => t.Id == order.MealPlanTemplateId, ct);
        var existing = await db.Set<SubscriptionServiceDay>().AsNoTracking()
            .Where(d => d.OrderId == orderId || d.SubscriptionId == orderId)
            .Select(d => new { d.Id, d.OrderId, d.SubscriptionId, d.ServiceDate, d.ServiceSequence, d.SelectionLocked }).ToListAsync(ct);
        var dates = SubscriptionMenuRules.ServiceDates(order.StartDate, order.EndDate, order.DeliveryDays.Select(d => d.DayOfWeek).ToHashSet())
            .Select((date, index) => new { ServiceDate = date, ServiceSequence = index + 1, MenuWeekNumber = SubscriptionMenuRules.MenuWeek(index + 1, template.CycleWeeks) })
            .Where(d => d.ServiceDate >= today).ToArray();
        var conflicts = existing.Any(d => d.OrderId != orderId) || dates.Any(d => existing.Any(e => e.ServiceSequence == d.ServiceSequence && e.ServiceDate != d.ServiceDate));
        return Ok(new { orderId, order.CustomerProfileId, order.OrderNumber, order.Status, order.StartDate, order.EndDate,
            TemplateId = template.Id, TemplateVersion = template.VersionNumber, QatarToday = today,
            CanInitialize = order.Status is "CONFIRMED" or "ACTIVE" && order.EndDate >= today &&
                dates.Length > 0 && order.DeliveryDays.All(d => d.DayOfWeek != 5) && !conflicts,
            HasLinkOrSequenceConflicts = conflicts, ExistingServiceDays = existing,
            MissingServiceDays = dates.Where(d => !existing.Any(e => e.ServiceDate == d.ServiceDate)).ToArray() });
    }

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
        try { await meals.InitializeAsync(order, ct); }
        catch (UpcomingMealException error)
        {
            var problem = new ProblemDetails { Status = error.StatusCode, Title = error.Message };
            problem.Extensions["code"] = error.Code;
            return StatusCode(error.StatusCode, problem);
        }
        return Ok(new { serviceDays = await db.Set<SubscriptionServiceDay>().CountAsync(d => d.OrderId == orderId, ct) });
    }
}
