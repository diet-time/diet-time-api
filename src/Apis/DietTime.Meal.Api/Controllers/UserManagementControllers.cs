using Asp.Versioning;
using DietTime.Application;
using DietTime.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace DietTime.Meal.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
[ApiVersion("1")]
[Authorize]
public sealed class UserProfilesController(IUserProfileService service, ILogger<UserProfilesController> logger) : ControllerBase
{
    /// <summary>
    /// Get all user profiles with optional filtering
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<UserProfileResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize < 1 || pageSize > 100)
            return BadRequest(new { message = "Invalid page or pageSize" });

        var result = await service.GetAllAsync(page, pageSize, status, cancellationToken);
        return Ok(ApiResponse<PagedResult<UserProfileResponse>>.Ok(result));
    }

    /// <summary>
    /// Get a specific user profile by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(UserProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var result = await service.GetByIdAsync(id, cancellationToken);
        if (result == null)
            return NotFound(new { message = "User profile not found" });

        return Ok(ApiResponse<UserProfileResponse>.Ok(result));
    }

    /// <summary>
    /// Create a new user profile
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Guid), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateUserProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var result = await service.CreateAsync(request, userId, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result }, result);
    }

    /// <summary>
    /// Update an existing user profile
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateUserProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var result = await service.UpdateAsync(id, request, userId, cancellationToken);
        if (!result)
            return NotFound(new { message = "User profile not found" });

        return NoContent();
    }

    /// <summary>
    /// Delete a user profile
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var result = await service.DeleteAsync(id, cancellationToken);
        if (!result)
            return NotFound(new { message = "User profile not found" });

        return NoContent();
    }
}

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
[ApiVersion("1")]
[Authorize]
public sealed class CustomersController(ICustomerService service, ILogger<CustomersController> logger) : ControllerBase
{
    /// <summary>
    /// Get all customers with optional filtering
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<CustomerResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize < 1 || pageSize > 100)
            return BadRequest(new { message = "Invalid page or pageSize" });

        var result = await service.GetAllAsync(page, pageSize, status, cancellationToken);
        return Ok(ApiResponse<PagedResult<CustomerResponse>>.Ok(result));
    }

    /// <summary>
    /// Get a specific customer by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(CustomerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var result = await service.GetByIdAsync(id, cancellationToken);
        if (result == null)
            return NotFound(new { message = "Customer not found" });

        return Ok(ApiResponse<CustomerResponse>.Ok(result));
    }

    /// <summary>
    /// Create a new customer
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Guid), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var result = await service.CreateAsync(request, userId, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result }, result);
    }

    /// <summary>
    /// Update an existing customer
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var result = await service.UpdateAsync(id, request, userId, cancellationToken);
        if (!result)
            return NotFound(new { message = "Customer not found" });

        return NoContent();
    }

    /// <summary>
    /// Delete a customer
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var result = await service.DeleteAsync(id, cancellationToken);
        if (!result)
            return NotFound(new { message = "Customer not found" });

        return NoContent();
    }

    /// <summary>
    /// Create a new customer with associated user profile (customer registration)
    /// </summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(Guid), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [AllowAnonymous]
    public async Task<IActionResult> CreateWithUser(
        [FromBody] dynamic request,
        CancellationToken cancellationToken = default)
    {
        // This would need proper model binding - simplified for now
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var createdBy = userId != null ? Guid.Parse(userId) : (Guid?)null;

        // Implementation would parse the request and call service.CreateWithUserAsync
        return CreatedAtAction(nameof(GetById), new { id = Guid.Empty }, Guid.Empty);
    }
}

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
[ApiVersion("1")]
[Authorize]
public sealed class RolesController(IApplicationRoleService service, ILogger<RolesController> logger) : ControllerBase
{
    /// <summary>
    /// Get all roles
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ApplicationRoleResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize < 1 || pageSize > 100)
            return BadRequest(new { message = "Invalid page or pageSize" });

        var result = await service.GetAllAsync(page, pageSize, cancellationToken);
        return Ok(ApiResponse<PagedResult<ApplicationRoleResponse>>.Ok(result));
    }

    /// <summary>
    /// Get a specific role by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ApplicationRoleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var result = await service.GetByIdAsync(id, cancellationToken);
        if (result == null)
            return NotFound(new { message = "Role not found" });

        return Ok(ApiResponse<ApplicationRoleResponse>.Ok(result));
    }

    /// <summary>
    /// Create a new role
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Guid), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateApplicationRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var result = await service.CreateAsync(request, userId, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result }, result);
    }

    /// <summary>
    /// Update an existing role
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateApplicationRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var modifiedBy = userId != null ? Guid.Parse(userId) : (Guid?)null;

        var result = await service.UpdateAsync(id, request, userId, cancellationToken);
        if (!result)
            return NotFound(new { message = "Role not found" });

        return NoContent();
    }

    /// <summary>
    /// Delete a role
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var result = await service.DeleteAsync(id, cancellationToken);
        if (!result)
            return NotFound(new { message = "Role not found" });

        return NoContent();
    }
}

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
[ApiVersion("1")]
[Authorize]
public sealed class MenusController(IMenuService service, ILogger<MenusController> logger) : ControllerBase
{
    /// <summary>
    /// Get all menus with optional filtering by menu level
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<MenuResponse>), StatusCodes.Status200OK)]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] int? menuLevel = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize < 1 || pageSize > 100)
            return BadRequest(new { message = "Invalid page or pageSize" });

        var result = await service.GetAllAsync(page, pageSize, menuLevel, cancellationToken);
        return Ok(ApiResponse<PagedResult<MenuResponse>>.Ok(result));
    }

    /// <summary>
    /// Get a specific menu by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(MenuResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var result = await service.GetByIdAsync(id, cancellationToken);
        if (result == null)
            return NotFound(new { message = "Menu not found" });

        return Ok(ApiResponse<MenuResponse>.Ok(result));
    }

    /// <summary>
    /// Get all main menus (menu level 1)
    /// </summary>
    [HttpGet("main/list")]
    [ProducesResponseType(typeof(IReadOnlyList<MenuResponse>), StatusCodes.Status200OK)]
    [AllowAnonymous]
    public async Task<IActionResult> GetMainMenus(CancellationToken cancellationToken = default)
    {
        var result = await service.GetMainMenusAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<MenuResponse>>.Ok(result));
    }

    /// <summary>
    /// Get sub menus for a specific parent menu
    /// </summary>
    [HttpGet("{parentMenuId}/submenu")]
    [ProducesResponseType(typeof(IReadOnlyList<MenuResponse>), StatusCodes.Status200OK)]
    [AllowAnonymous]
    public async Task<IActionResult> GetSubMenus(
        Guid parentMenuId,
        CancellationToken cancellationToken = default)
    {
        var result = await service.GetSubMenusByParentAsync(parentMenuId, cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<MenuResponse>>.Ok(result));
    }

}

[ApiController]
[Route("api/v{version:apiVersion}/role-menus")]
[ApiVersion("1")]
[Authorize]
public sealed class RoleMenusController(IRoleMenuMappingService service, ILogger<RoleMenusController> logger) : ControllerBase
{
    /// <summary>
    /// Get all menus assigned to a specific role
    /// </summary>
    [HttpGet("{roleId}")]
    [ProducesResponseType(typeof(RoleMenusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRoleMenus(
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await service.GetRoleMenusAsync(roleId, cancellationToken);
            return Ok(ApiResponse<RoleMenusResponse>.Ok(result));
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { message = "Role not found" });
        }
    }

    /// <summary>
    /// Assign a menu to a role
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Guid), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AssignMenuToRole(
        [FromBody] CreateRoleMenuMappingRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetRoleMenus), new { roleId = request.RoleId }, result);
    }

    /// <summary>
    /// Remove a menu from a role
    /// </summary>
    [HttpDelete("{roleId}/{menuId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveMenuFromRole(
        Guid roleId,
        Guid menuId,
        CancellationToken cancellationToken = default)
    {
        var result = await service.DeleteAsync(roleId, menuId, cancellationToken);
        if (!result)
            return NotFound(new { message = "Role-menu mapping not found" });

        return NoContent();
    }
}

[ApiController]
[Route("api/v{version:apiVersion}/user-menus")]
[ApiVersion("1")]
[Authorize]
public sealed class UserMenusController(IUserMenuService service, ILogger<UserMenusController> logger) : ControllerBase
{
    /// <summary>
    /// Get all menus available to a user based on their roles (for non-customers only)
    /// </summary>
    [HttpGet("{userId}")]
    [ProducesResponseType(typeof(IReadOnlyList<MenuResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserMenus(
        Guid userId,
        [FromQuery] int? menuLevel = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MenuResponse> result;

        result = await service.GetMenusByUserIdAsync(userId, cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<MenuResponse>>.Ok(result));
    }

    /// <summary>
    /// Get current user's menus
    /// </summary>
    [HttpGet("me/menus")]
    [ProducesResponseType(typeof(IReadOnlyList<MenuResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCurrentUserMenus(
        [FromQuery] int? menuLevel = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
            return Unauthorized();

        var userGuid = Guid.Parse(userId);
        IReadOnlyList<MenuResponse> result;

        result = await service.GetMenusByUserIdAsync(userGuid, cancellationToken);

        return Ok(ApiResponse<IReadOnlyList<MenuResponse>>.Ok(result));
    }
}
