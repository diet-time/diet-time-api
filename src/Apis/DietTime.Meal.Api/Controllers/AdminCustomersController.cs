using DietTime.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/customers")]
[Route("api/v1/admin/customers")]
public sealed class AdminCustomersController(AdminCustomerService customers, StaffOrderService orders) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string? search = null, int pageNumber = 1, int pageSize = 20, CancellationToken cancellationToken = default, string sort = "name_asc")
    {
        if (pageNumber < 1 || pageSize is < 1 or > 100 || search?.Length > 200 || !new[] { "name_asc", "name_desc", "booked_desc", "latest_order_desc", "newest_desc" }.Contains(sort))
            return BadRequest(new ProblemDetails { Detail = "Use a valid page, page size (1–100) and search text up to 200 characters." });
        return Ok(await customers.ListAsync(search, pageNumber, pageSize, cancellationToken, sort));
    }

    [HttpGet("{customerId:guid}")]
    public async Task<IActionResult> Get(Guid customerId, CancellationToken ct) =>
        await customers.GetAsync(customerId, ct) is { } customer ? Ok(customer) : NotFound();

    [HttpGet("{customerId:guid}/orders")]
    public async Task<IActionResult> Orders(Guid customerId, int pageNumber = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1 || pageSize is < 1 or > 100) return BadRequest();
        if (!await customers.ExistsAsync(customerId, cancellationToken)) return NotFound();
        return Ok(await orders.ListAsync(null, pageNumber, pageSize, cancellationToken, customerProfileId: customerId));
    }
}
