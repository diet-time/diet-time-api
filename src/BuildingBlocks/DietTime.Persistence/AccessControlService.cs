using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class AccessControlService(
    DietTimeDbContext db,
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    TimeProvider clock) : IAccessControlService
{
    public async Task<IReadOnlyList<ScreenPermissionResponse>> GetScreensAsync(CancellationToken cancellationToken) =>
        await db.Menus.AsNoTracking()
            .OrderBy(x => x.DisplayOrder)
            .Select(x => ToScreen(x, false, false))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AccessRoleResponse>> GetRolesAsync(CancellationToken cancellationToken)
    {
        var roles = await db.ApplicationRoles.AsNoTracking().OrderBy(x => x.RoleName).ToListAsync(cancellationToken);
        var screens = await db.Menus.AsNoTracking().OrderBy(x => x.DisplayOrder).ToListAsync(cancellationToken);
        var mappings = await db.RoleMenuMappings.AsNoTracking().ToListAsync(cancellationToken);

        return roles.Select(role => new AccessRoleResponse(
            role.Id, role.RoleName, role.Description, role.IsActive,
            screens.Select(screen =>
            {
                var permission = mappings.FirstOrDefault(x => x.RoleId == role.Id && x.MenuId == screen.Id);
                return ToScreen(screen, permission?.CanRead ?? false, permission?.CanWrite ?? false);
            }).ToList())).ToList();
    }

    public async Task<Guid> CreateRoleAsync(SaveAccessRoleRequest request, string actor, CancellationToken cancellationToken)
    {
        var roleName = Require(request.RoleName, "Role name");
        if (await db.ApplicationRoles.AnyAsync(x => x.RoleName.ToUpper() == roleName.ToUpper(), cancellationToken))
            throw new InvalidOperationException($"Role '{roleName}' already exists.");

        var identityRole = await roleManager.FindByNameAsync(roleName);
        if (identityRole == null)
        {
            identityRole = new IdentityRole<Guid>(roleName);
            EnsureSucceeded(await roleManager.CreateAsync(identityRole));
        }

        var now = clock.GetUtcNow();
        var role = new ApplicationRole
        {
            Id = Guid.NewGuid(), RoleName = roleName, Description = request.Description?.Trim(),
            IsActive = request.IsActive, CreatedAt = now, UpdatedAt = now, CreatedBy = actor
        };
        db.ApplicationRoles.Add(role);
        await ReplacePermissionsAsync(role.Id, request.Screens, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return role.Id;
    }

    public async Task<bool> UpdateRoleAsync(Guid roleId, SaveAccessRoleRequest request, string actor, CancellationToken cancellationToken)
    {
        var role = await db.ApplicationRoles.FirstOrDefaultAsync(x => x.Id == roleId, cancellationToken);
        if (role == null) return false;

        var roleName = Require(request.RoleName, "Role name");
        if (await db.ApplicationRoles.AnyAsync(x => x.Id != roleId && x.RoleName.ToUpper() == roleName.ToUpper(), cancellationToken))
            throw new InvalidOperationException($"Role '{roleName}' already exists.");

        var identityRole = await roleManager.FindByNameAsync(role.RoleName);
        if (identityRole == null)
        {
            identityRole = new IdentityRole<Guid>(roleName);
            EnsureSucceeded(await roleManager.CreateAsync(identityRole));
        }
        else if (!string.Equals(role.RoleName, roleName, StringComparison.OrdinalIgnoreCase))
        {
            identityRole.Name = roleName;
            EnsureSucceeded(await roleManager.UpdateAsync(identityRole));
        }

        var now = clock.GetUtcNow();
        role.RoleName = roleName;
        role.Description = request.Description?.Trim();
        role.IsActive = request.IsActive;
        role.UpdatedAt = now;
        role.UpdatedBy = actor;
        await ReplacePermissionsAsync(roleId, request.Screens, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<AccessUserResponse>> GetUsersAsync(CancellationToken cancellationToken)
    {
        var profiles = await db.UserProfiles.AsNoTracking().Where(x => !x.IsCustomer)
            .OrderBy(x => x.FirstName).ThenBy(x => x.LastName).ToListAsync(cancellationToken);
        var applicationRoles = await db.ApplicationRoles.AsNoTracking().ToListAsync(cancellationToken);
        var result = new List<AccessUserResponse>(profiles.Count);
        foreach (var profile in profiles)
        {
            var user = await userManager.FindByIdAsync(profile.UserId.ToString());
            if (user == null) continue;
            var roleNames = await userManager.GetRolesAsync(user);
            var roleIds = applicationRoles.Where(x => roleNames.Contains(x.RoleName, StringComparer.OrdinalIgnoreCase)).Select(x => x.Id).ToList();
            result.Add(ToUser(profile, user, roleIds, roleNames.ToList()));
        }
        return result;
    }

    public async Task<Guid> CreateUserAsync(CreateAccessUserRequest request, string actor, CancellationToken cancellationToken)
    {
        var roles = await GetSelectedRolesAsync(request.RoleIds, cancellationToken);
        var email = Require(request.Email, "Email").ToLowerInvariant();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        EnsureSucceeded(await userManager.CreateAsync(user, request.Password));

        try
        {
            if (roles.Count > 0) EnsureSucceeded(await userManager.AddToRolesAsync(user, roles.Select(x => x.RoleName)));
            var now = clock.GetUtcNow();
            var profile = new UserProfile
            {
                Id = Guid.NewGuid(), UserId = user.Id, FirstName = Require(request.FirstName, "First name"),
                LastName = Require(request.LastName, "Last name"), Mobile = request.Mobile?.Trim(),
                Status = request.IsActive ? "ACTIVE" : "INACTIVE", IsActive = request.IsActive,
                CreatedBy = actor, CreatedAt = now, ModifiedAt = now
            };
            db.UserProfiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);
            return profile.Id;
        }
        catch
        {
            await userManager.DeleteAsync(user);
            throw;
        }
    }

    public async Task<bool> UpdateUserAsync(Guid profileId, UpdateAccessUserRequest request, string actor, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles.FirstOrDefaultAsync(x => x.Id == profileId && !x.IsCustomer, cancellationToken);
        if (profile == null) return false;
        var user = await userManager.FindByIdAsync(profile.UserId.ToString());
        if (user == null) return false;
        var roles = await GetSelectedRolesAsync(request.RoleIds, cancellationToken);
        var email = Require(request.Email, "Email").ToLowerInvariant();

        if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            EnsureSucceeded(await userManager.SetEmailAsync(user, email));
            EnsureSucceeded(await userManager.SetUserNameAsync(user, email));
            user.EmailConfirmed = true;
            EnsureSucceeded(await userManager.UpdateAsync(user));
        }
        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            EnsureSucceeded(await userManager.ResetPasswordAsync(user, token, request.Password));
        }

        var currentRoles = await userManager.GetRolesAsync(user);
        var desiredRoles = roles.Select(x => x.RoleName).ToList();
        var removed = currentRoles.Except(desiredRoles, StringComparer.OrdinalIgnoreCase).ToList();
        var added = desiredRoles.Except(currentRoles, StringComparer.OrdinalIgnoreCase).ToList();
        if (removed.Count > 0) EnsureSucceeded(await userManager.RemoveFromRolesAsync(user, removed));
        if (added.Count > 0) EnsureSucceeded(await userManager.AddToRolesAsync(user, added));

        profile.FirstName = Require(request.FirstName, "First name");
        profile.LastName = Require(request.LastName, "Last name");
        profile.Mobile = request.Mobile?.Trim();
        profile.IsActive = request.IsActive;
        profile.Status = request.IsActive ? "ACTIVE" : "INACTIVE";
        profile.ModifiedBy = actor;
        profile.ModifiedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ScreenPermissionResponse>> GetUserScreensAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user == null) return [];
        var roleNames = await userManager.GetRolesAsync(user);
        var screens = await db.Menus.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.DisplayOrder).ToListAsync(cancellationToken);
        if (roleNames.Contains("Admin", StringComparer.OrdinalIgnoreCase))
            return screens.Select(x => ToScreen(x, true, true)).ToList();

        var roleIds = await db.ApplicationRoles.Where(x => x.IsActive && roleNames.Contains(x.RoleName)).Select(x => x.Id).ToListAsync(cancellationToken);
        var permissions = await db.RoleMenuMappings.AsNoTracking().Where(x => roleIds.Contains(x.RoleId)).ToListAsync(cancellationToken);
        return screens.Select(screen => ToScreen(
                screen,
                permissions.Any(x => x.MenuId == screen.Id && x.CanRead),
                permissions.Any(x => x.MenuId == screen.Id && x.CanWrite)))
            .Where(x => x.CanRead).ToList();
    }

    private async Task<List<ApplicationRole>> GetSelectedRolesAsync(IReadOnlyList<Guid> roleIds, CancellationToken cancellationToken)
    {
        var distinctIds = roleIds.Distinct().ToList();
        var roles = await db.ApplicationRoles.Where(x => distinctIds.Contains(x.Id) && x.IsActive).ToListAsync(cancellationToken);
        if (roles.Count != distinctIds.Count) throw new InvalidOperationException("One or more selected roles are invalid or inactive.");
        foreach (var role in roles)
            if (await roleManager.FindByNameAsync(role.RoleName) == null)
                EnsureSucceeded(await roleManager.CreateAsync(new IdentityRole<Guid>(role.RoleName)));
        return roles;
    }

    private async Task ReplacePermissionsAsync(Guid roleId, IReadOnlyList<ScreenPermissionRequest> requested, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var normalized = requested.Where(x => x.CanRead || x.CanWrite).GroupBy(x => x.ScreenId)
            .Select(x => x.Last() with { CanRead = x.Last().CanRead || x.Last().CanWrite }).ToList();
        var validIds = await db.Menus.Where(x => normalized.Select(p => p.ScreenId).Contains(x.Id)).Select(x => x.Id).ToListAsync(cancellationToken);
        if (validIds.Count != normalized.Count) throw new InvalidOperationException("One or more selected screens do not exist.");
        var old = await db.RoleMenuMappings.Where(x => x.RoleId == roleId).ToListAsync(cancellationToken);
        db.RoleMenuMappings.RemoveRange(old);
        db.RoleMenuMappings.AddRange(normalized.Select(x => new RoleMenuMapping
        {
            Id = Guid.NewGuid(), RoleId = roleId, MenuId = x.ScreenId,
            CanRead = x.CanRead, CanWrite = x.CanWrite, CreatedAt = now
        }));
    }

    private static ScreenPermissionResponse ToScreen(Menu x, bool read, bool write) => new(
        x.Id, x.MainMenuCode, x.MainMenuName, x.SubMenuCode, x.SubMenuName,
        x.RouteUrl, x.Icon, x.DisplayOrder, x.IsActive, read, write);

    private static AccessUserResponse ToUser(UserProfile profile, ApplicationUser user, IReadOnlyList<Guid> roleIds, IReadOnlyList<string> roleNames) => new(
        profile.Id, profile.UserId, user.Email ?? "", profile.FirstName, profile.LastName,
        profile.Mobile, profile.Status, profile.IsActive, roleIds, roleNames);

    private static string Require(string value, string label) =>
        !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new InvalidOperationException($"{label} is required.");

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(x => x.Description)));
    }
}
