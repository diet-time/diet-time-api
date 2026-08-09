using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[Authorize]
[Route("api/v{version:apiVersion}/customer-profiles/{customerProfileId:guid}/addresses")]
public sealed class CustomerAddressesController(ICustomerAddressService addresses) : ControllerBase
{
    /// <summary>Saves a delivery address against the authenticated customer's profile.</summary>
    /// <remarks>
    /// The first active address is made the default automatically. Example:
    /// <code>{ "addressName": "Home", "addressType": "HOME", "buildingNo": "126", "streetNo": "960", "zoneNo": "91", "area": "Al Wakrah", "directions": "Call me when you arrive", "latitude": 25.1712345, "longitude": 51.6034567, "formattedAddress": "Zone 91, Al Wakrah, Qatar", "isDefault": true }</code>
    /// </remarks>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json", "application/problem+json")]
    [ProducesResponseType(typeof(CustomerAddressResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create(
        Guid customerProfileId, UpsertCustomerAddressRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await addresses.CreateAsync(customerProfileId, userId, request, cancellationToken);
        if (result.Status != CustomerAddressWriteStatus.Success)
            return NotFoundProblem("customer_profile_not_found", "Customer profile not found.");

        return CreatedAtAction(nameof(Get),
            new { version = "1", customerProfileId, addressId = result.Address!.Id }, result.Address);
    }

    /// <summary>Returns the customer's active saved addresses, with the default first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(CustomerAddressListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAll(Guid customerProfileId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var items = await addresses.GetAllAsync(customerProfileId, userId, cancellationToken);
        return items is null
            ? NotFoundProblem("customer_profile_not_found", "Customer profile not found.")
            : Ok(new CustomerAddressListResponse(items));
    }

    /// <summary>Returns one active address belonging to the supplied customer profile.</summary>
    [HttpGet("{addressId:guid}")]
    [ProducesResponseType(typeof(CustomerAddressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        Guid customerProfileId, Guid addressId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var address = await addresses.GetAsync(customerProfileId, addressId, userId, cancellationToken);
        return address is null
            ? NotFoundProblem("address_not_found", "Customer address not found.")
            : Ok(address);
    }

    /// <summary>Updates an active customer address.</summary>
    /// <remarks>Setting <c>isDefault</c> to true removes the default flag from the customer's other addresses.</remarks>
    [HttpPut("{addressId:guid}")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(CustomerAddressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid customerProfileId, Guid addressId, UpsertCustomerAddressRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await addresses.UpdateAsync(customerProfileId, addressId, userId, request, cancellationToken);
        return WriteResult(result);
    }

    /// <summary>Soft-deletes an address and promotes another address when the default is removed.</summary>
    [HttpDelete("{addressId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        Guid customerProfileId, Guid addressId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var status = await addresses.DeleteAsync(customerProfileId, addressId, userId, cancellationToken);
        return status switch
        {
            CustomerAddressWriteStatus.Success => NoContent(),
            CustomerAddressWriteStatus.CustomerNotFound => NotFoundProblem("customer_profile_not_found", "Customer profile not found."),
            _ => NotFoundProblem("address_not_found", "Customer address not found.")
        };
    }

    /// <summary>Makes the selected active address the customer's sole default address.</summary>
    [HttpPatch("{addressId:guid}/default")]
    [ProducesResponseType(typeof(CustomerAddressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefault(
        Guid customerProfileId, Guid addressId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await addresses.SetDefaultAsync(customerProfileId, addressId, userId, cancellationToken);
        return WriteResult(result);
    }

    private IActionResult WriteResult(CustomerAddressWriteResult result) => result.Status switch
    {
        CustomerAddressWriteStatus.Success => Ok(result.Address),
        CustomerAddressWriteStatus.CustomerNotFound => NotFoundProblem("customer_profile_not_found", "Customer profile not found."),
        _ => NotFoundProblem("address_not_found", "Customer address not found.")
    };

    private ObjectResult NotFoundProblem(string code, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Resource not found",
            Detail = detail,
            Instance = HttpContext.Request.Path
        };
        problem.Extensions["code"] = code;
        return NotFound(problem);
    }

    private bool TryGetUserId(out Guid userId)
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(value, out userId);
    }
}
