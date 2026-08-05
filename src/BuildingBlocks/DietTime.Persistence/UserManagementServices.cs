using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class UserProfileService(
    DietTimeDbContext db,
    UserManager<ApplicationUser> userManager,
    TimeProvider clock) : IUserProfileService
{
    public async Task<UserProfileResponse?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        return profile == null ? null : MapToResponse(profile, null);
    }

    public async Task<UserProfileResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (profile == null) return null;

        var createdByUser =  await userManager.FindByIdAsync(profile.CreatedBy.ToString());

        var modifiedByUser = await userManager.FindByIdAsync(profile.ModifiedBy!.ToString());

        return MapToResponse(profile, await userManager.FindByIdAsync(profile.UserId.ToString()), createdByUser?.UserName, modifiedByUser?.UserName);
    }

    public async Task<PagedResult<UserProfileResponse>> GetAllAsync(int page, int pageSize, string? status = null, CancellationToken cancellationToken = default)
    {
        var query = db.UserProfiles.AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(p => p.Status == status);

        var total = await query.CountAsync(cancellationToken);
        var profiles = await query
            .AsNoTracking()
            .OrderBy(p => p.FirstName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var responses = new List<UserProfileResponse>();
        foreach (var profile in profiles)
        {
            var user = await userManager.FindByIdAsync(profile.UserId.ToString());
            responses.Add(MapToResponse(profile, user));
        }

        var meta = new PaginationMeta(page, pageSize, total, (total + pageSize - 1) / pageSize);
        return new(responses, meta);
    }

    public async Task<Guid> CreateAsync(CreateUserProfileRequest request, string? createdBy, CancellationToken cancellationToken)
    {
        var newUser = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(newUser, request.Password);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Failed to create user: {string.Join(", ", result.Errors.Select(e => e.Description))}");

        var profile = new UserProfile
        {
            Id = Guid.NewGuid(),
            UserId = Guid.Parse(newUser.Id.ToString()),
            FirstName = request.FirstName,
            LastName = request.LastName,
            Status = request.Status,
            IsActive = request.IsActive,
            Mobile = request.Mobile,
            CreatedBy = createdBy ?? "SYSTEM",
            CreatedAt = clock.GetUtcNow(),
            ModifiedAt = clock.GetUtcNow()
        };

        db.UserProfiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);

        return profile.Id;
    }

    public async Task<bool> UpdateAsync(Guid id, UpdateUserProfileRequest request, string? modifiedBy, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (profile == null) return false;

        profile.FirstName = request.FirstName;
        profile.LastName = request.LastName;
        profile.Mobile = request.Mobile;
        profile.Status = request.Status;
        profile.IsActive = request.IsActive;
        profile.ModifiedBy = modifiedBy;
        profile.ModifiedAt = clock.GetUtcNow();

        db.UserProfiles.Update(profile);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (profile == null) return false;

        var user = await userManager.FindByIdAsync(profile.UserId.ToString());
        if (user != null)
            await userManager.DeleteAsync(user);

        db.UserProfiles.Remove(profile);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    private static UserProfileResponse MapToResponse(
        UserProfile profile,
        ApplicationUser? user,
        string? createdByName = null,
        string? modifiedByName = null)
    {
        return new(
            profile.Id,
            profile.UserId,
            profile.FirstName,
            profile.LastName,
            profile.FullName,
            user?.Email ?? "",
            profile.Mobile,
            profile.Status,
            profile.IsActive,
            profile.IsCustomer,
            profile.CustomerId,
            createdByName,
            profile.CreatedAt,
            modifiedByName,
            profile.ModifiedAt!.Value);
    }
}

public sealed class CustomerService(
    DietTimeDbContext db,
    UserManager<ApplicationUser> userManager,
    IUserProfileService userProfileService,
    TimeProvider clock) : ICustomerService
{
    public async Task<CustomerResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var customer = await db.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        return customer == null ? null : MapToResponse(customer);
    }

    public async Task<PagedResult<CustomerResponse>> GetAllAsync(int page, int pageSize, string? status = null, CancellationToken cancellationToken = default)
    {
        var query = db.Customers.AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(c => c.Status == status);

        var total = await query.CountAsync(cancellationToken);
        var customers = await query
            .AsNoTracking()
            .OrderBy(c => c.CustomerName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var meta = new PaginationMeta(page, pageSize, total, (total + pageSize - 1) / pageSize);
        return new(customers.Select(MapToResponse).ToList(), meta);
    }

    public async Task<Guid> CreateAsync(CreateCustomerRequest request, string? createdBy, CancellationToken cancellationToken)
    {
        var bmi = CalculateBMI(request.Weight, request.Height);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            CustomerName = request.CustomerName,
            Age = request.Age,
            Mobile = request.Mobile,
            Email = request.Email,
            Status = request.Status,
            IsActive = request.IsActive,
            Weight = request.Weight,
            Height = request.Height,
            BMI = bmi,
            CreatedBy = createdBy ?? "SYSTEM",
            CreatedAt = clock.GetUtcNow(),
            UpdatedAt = clock.GetUtcNow()
        };

        db.Customers.Add(customer);
        await db.SaveChangesAsync(cancellationToken);

        return customer.Id;
    }

    public async Task<bool> UpdateAsync(Guid id, UpdateCustomerRequest request, string? modifiedBy, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (customer == null) return false;

        customer.CustomerName = request.CustomerName;
        customer.Age = request.Age;
        customer.Mobile = request.Mobile;
        customer.Email = request.Email;
        customer.Status = request.Status;
        customer.IsActive = request.IsActive;
        customer.Weight = request.Weight;
        customer.Height = request.Height;
        customer.BMI = CalculateBMI(request.Weight, request.Height);
        customer.UpdatedBy = modifiedBy;
        customer.UpdatedAt = clock.GetUtcNow();

        db.Customers.Update(customer);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (customer == null) return false;

        db.Customers.Remove(customer);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<Guid> CreateWithUserAsync(
        CreateCustomerRequest customerRequest,
        CreateUserProfileRequest userRequest,
        string password,
        string? createdBy,
        CancellationToken cancellationToken)
    {
        var customerId = await CreateAsync(customerRequest, createdBy, cancellationToken);

        var updatedUserRequest = userRequest with { Email = customerRequest.Email ?? userRequest.Email };
        var userProfileId = await userProfileService.CreateAsync(updatedUserRequest, createdBy, cancellationToken);

        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == userProfileId, cancellationToken);
        if (profile != null)
        {
            profile.IsCustomer = true;
            profile.CustomerId = customerId;
            db.UserProfiles.Update(profile);
            await db.SaveChangesAsync(cancellationToken);
        }

        return customerId;
    }

    private static CustomerResponse MapToResponse(Customer customer)
    {
        return new(
            customer.Id,
            customer.CustomerName,
            customer.Age,
            customer.Mobile,
            customer.Email,
            customer.Status,
            customer.IsActive,
            customer.Weight,
            customer.Height,
            customer.BMI,
            customer.CreatedAt,
            customer.UpdatedAt);
    }

    private static decimal? CalculateBMI(decimal? weight, decimal? height)
    {
        if (!weight.HasValue || !height.HasValue || height.Value == 0)
            return null;

        // BMI = weight (kg) / (height (m))^2
        var heightInMeters = height.Value / 100;
        return weight / (heightInMeters * heightInMeters);
    }
}

public sealed class ApplicationRoleService(
    DietTimeDbContext db,
    RoleManager<IdentityRole<Guid>> roleManager,
    UserManager<ApplicationUser> userManager,
    TimeProvider clock) : IApplicationRoleService
{
    public async Task<ApplicationRoleResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var role = await db.ApplicationRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        return role == null ? null : MapToResponse(role);
    }

    public async Task<PagedResult<ApplicationRoleResponse>> GetAllAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = db.ApplicationRoles.AsQueryable();

        var total = await query.CountAsync(cancellationToken);
        var roles = await query
            .AsNoTracking()
            .OrderBy(r => r.RoleName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var meta = new PaginationMeta(page, pageSize, total, (total + pageSize - 1) / pageSize);
        return new(roles.Select(MapToResponse).ToList(), meta);
    }

    public async Task<Guid> CreateAsync(CreateApplicationRoleRequest request, string? createdBy, CancellationToken cancellationToken)
    {
        var identityRole = await roleManager.FindByNameAsync(request.RoleName);
        var identityRoleCreated = false;
        if (identityRole == null)
        {
            identityRole = new IdentityRole<Guid>(request.RoleName);
            var identityResult = await roleManager.CreateAsync(identityRole);
            if (!identityResult.Succeeded)
                throw new InvalidOperationException(string.Join(" ", identityResult.Errors.Select(x => x.Description)));
            identityRoleCreated = true;
        }

        var role = new ApplicationRole
        {
            Id = Guid.NewGuid(),
            RoleName = request.RoleName,
            Description = request.Description,
            IsActive = request.IsActive,
            CreatedBy = createdBy ?? "SYSTEM",
            CreatedAt = clock.GetUtcNow(),
            UpdatedAt = clock.GetUtcNow()
        };

        try
        {
            db.ApplicationRoles.Add(role);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (identityRoleCreated) await roleManager.DeleteAsync(identityRole);
            throw;
        }

        return role.Id;
    }

    public async Task<bool> UpdateAsync(Guid id, UpdateApplicationRoleRequest request, string? modifiedBy, CancellationToken cancellationToken)
    {
        var role = await db.ApplicationRoles.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (role == null) return false;

        var identityRole = await roleManager.FindByNameAsync(role.RoleName);
        if (identityRole == null)
        {
            var createResult = await roleManager.CreateAsync(new IdentityRole<Guid>(request.RoleName));
            if (!createResult.Succeeded)
                throw new InvalidOperationException(string.Join(" ", createResult.Errors.Select(x => x.Description)));
        }
        else if (!string.Equals(role.RoleName, request.RoleName, StringComparison.OrdinalIgnoreCase))
        {
            identityRole.Name = request.RoleName;
            var updateResult = await roleManager.UpdateAsync(identityRole);
            if (!updateResult.Succeeded)
                throw new InvalidOperationException(string.Join(" ", updateResult.Errors.Select(x => x.Description)));
        }

        role.RoleName = request.RoleName;
        role.Description = request.Description;
        role.IsActive = request.IsActive;
        role.UpdatedBy = modifiedBy;
        role.UpdatedAt = clock.GetUtcNow();

        db.ApplicationRoles.Update(role);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var role = await db.ApplicationRoles.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (role == null) return false;

        var identityRole = await roleManager.FindByNameAsync(role.RoleName);
        if (identityRole != null)
        {
            var identityResult = await roleManager.DeleteAsync(identityRole);
            if (!identityResult.Succeeded)
                throw new InvalidOperationException(string.Join(" ", identityResult.Errors.Select(x => x.Description)));
        }

        db.ApplicationRoles.Remove(role);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> AssignUserToRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user == null) return false;

        var role = await db.ApplicationRoles
            .FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role == null) return false;

        if (await roleManager.FindByNameAsync(role.RoleName) == null)
        {
            var createResult = await roleManager.CreateAsync(new IdentityRole<Guid>(role.RoleName));
            if (!createResult.Succeeded) return false;
        }

        var result = await userManager.AddToRoleAsync(user, role.RoleName);
        return result.Succeeded;
    }

    public async Task<bool> RemoveUserFromRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user == null) return false;

        var role = await db.ApplicationRoles
            .FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role == null) return false;

        var result = await userManager.RemoveFromRoleAsync(user, role.RoleName);
        return result.Succeeded;
    }

    private static ApplicationRoleResponse MapToResponse(ApplicationRole role)
    {
        return new(
            role.Id,
            role.RoleName,
            role.Description,
            role.IsActive,
            role.CreatedAt,
            role.UpdatedAt);
    }
}

public sealed class MenuService(DietTimeDbContext db) : IMenuService
{
    public async Task<MenuResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var menu = await db.Menus
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

        return menu == null ? null : MapToResponse(menu);
    }

    public async Task<PagedResult<MenuResponse>> GetAllAsync(int page, int pageSize, int? menuLevel = null, CancellationToken cancellationToken = default)
    {
        // Flat menu structure - menuLevel parameter is ignored
        var query = db.Menus.AsQueryable();

        var total = await query.CountAsync(cancellationToken);
        var menus = await query
            .AsNoTracking()
            .OrderBy(m => m.MainMenuCode)
            .ThenBy(m => m.DisplayOrder)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var meta = new PaginationMeta(page, pageSize, total, (total + pageSize - 1) / pageSize);
        return new(menus.Select(MapToResponse).ToList(), meta);
    }

    public async Task<IReadOnlyList<MenuResponse>> GetMainMenusAsync(CancellationToken cancellationToken)
    {
        var menus = await db.Menus
            .Where(m => m.IsActive)
            .AsNoTracking()
            .OrderBy(m => m.MainMenuCode)
            .ThenBy(m => m.DisplayOrder)
            .ToListAsync(cancellationToken);

        // Group by main menu code in memory and return unique main menus
        // (In a flat structure, we return all menu items for UI to group them)
        return menus.Select(MapToResponse).ToList();
    }

    public async Task<IReadOnlyList<MenuResponse>> GetSubMenusByParentAsync(Guid parentMenuId, CancellationToken cancellationToken)
    {
        // For flat menu structure, this method returns empty list
        // The UI will handle grouping of sub-menus by main menu code
        return await Task.FromResult(new List<MenuResponse>());
    }

    private static MenuResponse MapToResponse(Menu menu)
    {
        return new(
            menu.Id,
            menu.MainMenuCode,
            menu.MainMenuName,
            menu.SubMenuCode,
            menu.SubMenuName,
            menu.RouteUrl,
            menu.Icon,
            menu.DisplayOrder,
            menu.IsActive,
            menu.CreatedBy,
            menu.CreatedAt,
            menu.UpdatedBy,
            menu.UpdatedAt);
    }
}

public sealed class RoleMenuMappingService(DietTimeDbContext db) : IRoleMenuMappingService
{
    public async Task<PagedResult<RoleMenuMappingResponse>> GetByRoleIdAsync(Guid roleId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.RoleMenuMappings
            .Where(m => m.RoleId == roleId)
            .Include(m => m.Role)
            .Include(m => m.Menu)
            .AsQueryable();

        var total = await query.CountAsync(cancellationToken);
        var mappings = await query
            .AsNoTracking()
            .OrderBy(m => m.Menu.DisplayOrder)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var responses = mappings.Select(m => new RoleMenuMappingResponse(
            m.Id,
            m.RoleId,
            m.Role.RoleName,
            m.MenuId,
            m.Menu.SubMenuName,
            m.CreatedAt)).ToList();

        var meta = new PaginationMeta(page, pageSize, total, (total + pageSize - 1) / pageSize);
        return new(responses, meta);
    }

    public async Task<IReadOnlyList<MenuResponse>> GetMenusByRoleIdAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var menus = await db.RoleMenuMappings
            .Where(m => m.RoleId == roleId)
            .Include(m => m.Menu)
            .Select(m => m.Menu)
            .Where(m => m.IsActive)
            .AsNoTracking()
            .OrderBy(m => m.DisplayOrder)
            .ToListAsync(cancellationToken);

        return menus.Select(m => new MenuResponse(
            m.Id,
            m.MainMenuCode,
            m.MainMenuName,
            m.SubMenuCode,
            m.SubMenuName,
            m.RouteUrl,
            m.Icon,
            m.DisplayOrder,
            m.IsActive,
            m.CreatedBy,
            m.CreatedAt,
            m.UpdatedBy,
            m.UpdatedAt)).ToList();
    }

    public async Task<RoleMenusResponse> GetRoleMenusAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var role = await db.ApplicationRoles
            .FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role == null)
            throw new InvalidOperationException($"Role with ID {roleId} not found");

        var menus = await GetMenusByRoleIdAsync(roleId, cancellationToken);

        return new RoleMenusResponse(roleId, role.RoleName, menus);
    }

    public async Task<Guid> CreateAsync(CreateRoleMenuMappingRequest request, CancellationToken cancellationToken)
    {
        var mapping = new RoleMenuMapping
        {
            Id = Guid.NewGuid(),
            RoleId = request.RoleId,
            MenuId = request.MenuId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.RoleMenuMappings.Add(mapping);
        await db.SaveChangesAsync(cancellationToken);

        return mapping.Id;
    }

    public async Task<bool> DeleteAsync(Guid roleId, Guid menuId, CancellationToken cancellationToken)
    {
        var mapping = await db.RoleMenuMappings
            .FirstOrDefaultAsync(m => m.RoleId == roleId && m.MenuId == menuId, cancellationToken);

        if (mapping == null) return false;

        db.RoleMenuMappings.Remove(mapping);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> DeleteAllByRoleAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var mappings = await db.RoleMenuMappings
            .Where(m => m.RoleId == roleId)
            .ToListAsync(cancellationToken);

        if (mappings.Count == 0) return false;

        db.RoleMenuMappings.RemoveRange(mappings);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}

public sealed class UserMenuService(DietTimeDbContext db, UserManager<ApplicationUser> userManager) : IUserMenuService
{
    public async Task<IReadOnlyList<MenuResponse>> GetMenusByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        var userProfile = await db.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId && !p.IsCustomer, cancellationToken);

        if (userProfile == null)
            return [];

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user == null) return [];

        var roles = await userManager.GetRolesAsync(user);
        var roleIds = await db.ApplicationRoles
            .Where(r => roles.Contains(r.RoleName))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        if (roleIds.Count == 0) return [];

        var menus = await db.RoleMenuMappings
            .Where(m => roleIds.Contains(m.RoleId))
            .Select(m => m.Menu)
            .Where(m => m.IsActive)
            .AsNoTracking()
            .Distinct()
            .OrderBy(m => m.DisplayOrder)
            .ToListAsync(cancellationToken);

        return menus.Select(m => new MenuResponse(
            m.Id,
            m.MainMenuCode,
            m.MainMenuName,
            m.SubMenuCode,
            m.SubMenuName,
            m.RouteUrl,
            m.Icon,
            m.DisplayOrder,
            m.IsActive,
            m.CreatedBy,
            m.CreatedAt,
            m.UpdatedBy,
            m.UpdatedAt)).ToList();
    }
}
