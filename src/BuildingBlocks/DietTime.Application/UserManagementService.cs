using DietTime.Contracts;
using DietTime.Domain;

namespace DietTime.Application;

public interface IUserProfileService
{
    Task<UserProfileResponse?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken);
    Task<UserProfileResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<PagedResult<UserProfileResponse>> GetAllAsync(int page, int pageSize, string? status = null, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(CreateUserProfileRequest request, string? createdBy, CancellationToken cancellationToken);
    Task<bool> UpdateAsync(Guid id, UpdateUserProfileRequest request, string? modifiedBy, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public interface ICustomerService
{
    Task<CustomerResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<PagedResult<CustomerResponse>> GetAllAsync(int page, int pageSize, string? status = null, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(CreateCustomerRequest request, string? createdBy, CancellationToken cancellationToken);
    Task<bool> UpdateAsync(Guid id, UpdateCustomerRequest request, string? modifiedBy, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
    Task<Guid> CreateWithUserAsync(CreateCustomerRequest customerRequest, CreateUserProfileRequest userRequest, string password, string? createdBy, CancellationToken cancellationToken);
}

public interface IApplicationRoleService
{
    Task<ApplicationRoleResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<PagedResult<ApplicationRoleResponse>> GetAllAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(CreateApplicationRoleRequest request, string? createdBy, CancellationToken cancellationToken);
    Task<bool> UpdateAsync(Guid id, UpdateApplicationRoleRequest request, string? modifiedBy, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> AssignUserToRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken);
    Task<bool> RemoveUserFromRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken);
}

public interface IMenuService
{
    Task<MenuResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<PagedResult<MenuResponse>> GetAllAsync(int page, int pageSize, int? menuLevel = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MenuResponse>> GetMainMenusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MenuResponse>> GetSubMenusByParentAsync(Guid parentMenuId, CancellationToken cancellationToken);
}

public interface IRoleMenuMappingService
{
    Task<PagedResult<RoleMenuMappingResponse>> GetByRoleIdAsync(Guid roleId, int page, int pageSize, CancellationToken cancellationToken);
    Task<IReadOnlyList<MenuResponse>> GetMenusByRoleIdAsync(Guid roleId, CancellationToken cancellationToken);
    Task<RoleMenusResponse> GetRoleMenusAsync(Guid roleId, CancellationToken cancellationToken);
    Task<Guid> CreateAsync(CreateRoleMenuMappingRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid roleId, Guid menuId, CancellationToken cancellationToken);
    Task<bool> DeleteAllByRoleAsync(Guid roleId, CancellationToken cancellationToken);
}

public interface IUserMenuService
{
    Task<IReadOnlyList<MenuResponse>> GetMenusByUserIdAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IAccessControlService
{
    Task<IReadOnlyList<ScreenPermissionResponse>> GetScreensAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AccessRoleResponse>> GetRolesAsync(CancellationToken cancellationToken);
    Task<Guid> CreateRoleAsync(SaveAccessRoleRequest request, string actor, CancellationToken cancellationToken);
    Task<bool> UpdateRoleAsync(Guid roleId, SaveAccessRoleRequest request, string actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<AccessUserResponse>> GetUsersAsync(CancellationToken cancellationToken);
    Task<Guid> CreateUserAsync(CreateAccessUserRequest request, string actor, CancellationToken cancellationToken);
    Task<bool> UpdateUserAsync(Guid profileId, UpdateAccessUserRequest request, string actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<ScreenPermissionResponse>> GetUserScreensAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> HasScreenPermissionAsync(Guid userId, string routeUrl, bool requireWrite, CancellationToken cancellationToken);
}

public interface IPasswordService
{
    string HashPassword(string password);
    bool VerifyPassword(string password, string hash);
    Task<bool> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken);
    Task<bool> VerifyPasswordAsync(Guid userId, string password);
    Task<string> GeneratePasswordResetTokenAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> SetPasswordWithResetTokenAsync(Guid userId, string resetToken, string newPassword, CancellationToken cancellationToken);
}
