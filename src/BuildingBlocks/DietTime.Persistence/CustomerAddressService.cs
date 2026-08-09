using System.Data;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class CustomerAddressService(DietTimeDbContext db, TimeProvider clock)
    : ICustomerAddressService
{
    public async Task<CustomerAddressWriteResult> CreateAsync(
        Guid customerProfileId,
        Guid userId,
        UpsertCustomerAddressRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockOwnedProfileAsync(customerProfileId, userId, cancellationToken))
            return new(CustomerAddressWriteStatus.CustomerNotFound);

        var hasActiveAddress = await db.CustomerAddresses
            .AnyAsync(x => x.CustomerProfileId == customerProfileId && x.IsActive, cancellationToken);
        var makeDefault = request.IsDefault || !hasActiveAddress;
        if (makeDefault)
            await UnsetDefaultsAsync(customerProfileId, null, cancellationToken);

        var now = clock.GetUtcNow();
        var address = new CustomerAddress
        {
            Id = Guid.NewGuid(),
            CustomerProfileId = customerProfileId,
            IsActive = true,
            IsDefault = makeDefault,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = userId,
            UpdatedBy = userId,
            RowVersion = 1
        };
        Apply(address, request);
        db.CustomerAddresses.Add(address);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(CustomerAddressWriteStatus.Success, Map(address));
    }

    public async Task<IReadOnlyList<CustomerAddressResponse>?> GetAllAsync(
        Guid customerProfileId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await OwnedProfileExistsAsync(customerProfileId, userId, cancellationToken))
            return null;

        return await db.CustomerAddresses.AsNoTracking()
            .Where(x => x.CustomerProfileId == customerProfileId && x.IsActive)
            .OrderByDescending(x => x.IsDefault)
            .ThenByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Id)
            .Select(x => Map(x))
            .ToListAsync(cancellationToken);
    }

    public async Task<CustomerAddressResponse?> GetAsync(
        Guid customerProfileId, Guid addressId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await OwnedProfileExistsAsync(customerProfileId, userId, cancellationToken))
            return null;

        return await db.CustomerAddresses.AsNoTracking()
            .Where(x => x.Id == addressId && x.CustomerProfileId == customerProfileId && x.IsActive)
            .Select(x => Map(x))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<CustomerAddressWriteResult> UpdateAsync(
        Guid customerProfileId,
        Guid addressId,
        Guid userId,
        UpsertCustomerAddressRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockOwnedProfileAsync(customerProfileId, userId, cancellationToken))
            return new(CustomerAddressWriteStatus.CustomerNotFound);

        var address = await db.CustomerAddresses.SingleOrDefaultAsync(
            x => x.Id == addressId && x.CustomerProfileId == customerProfileId && x.IsActive,
            cancellationToken);
        if (address is null)
            return new(CustomerAddressWriteStatus.AddressNotFound);

        if (request.IsDefault)
            await UnsetDefaultsAsync(customerProfileId, addressId, cancellationToken);

        Apply(address, request);
        address.IsDefault = request.IsDefault || address.IsDefault;
        address.UpdatedAt = clock.GetUtcNow();
        address.UpdatedBy = userId;
        address.RowVersion++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(CustomerAddressWriteStatus.Success, Map(address));
    }

    public async Task<CustomerAddressWriteStatus> DeleteAsync(
        Guid customerProfileId, Guid addressId, Guid userId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockOwnedProfileAsync(customerProfileId, userId, cancellationToken))
            return CustomerAddressWriteStatus.CustomerNotFound;

        var address = await db.CustomerAddresses.SingleOrDefaultAsync(
            x => x.Id == addressId && x.CustomerProfileId == customerProfileId && x.IsActive,
            cancellationToken);
        if (address is null)
            return CustomerAddressWriteStatus.AddressNotFound;

        var wasDefault = address.IsDefault;
        address.IsActive = false;
        address.IsDefault = false;
        address.UpdatedAt = clock.GetUtcNow();
        address.UpdatedBy = userId;
        address.RowVersion++;

        if (wasDefault)
        {
            var replacement = await db.CustomerAddresses
                .Where(x => x.CustomerProfileId == customerProfileId && x.IsActive && x.Id != addressId)
                .OrderByDescending(x => x.UpdatedAt)
                .ThenBy(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (replacement is not null)
            {
                replacement.IsDefault = true;
                replacement.UpdatedAt = clock.GetUtcNow();
                replacement.UpdatedBy = userId;
                replacement.RowVersion++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CustomerAddressWriteStatus.Success;
    }

    public async Task<CustomerAddressWriteResult> SetDefaultAsync(
        Guid customerProfileId, Guid addressId, Guid userId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockOwnedProfileAsync(customerProfileId, userId, cancellationToken))
            return new(CustomerAddressWriteStatus.CustomerNotFound);

        var address = await db.CustomerAddresses.SingleOrDefaultAsync(
            x => x.Id == addressId && x.CustomerProfileId == customerProfileId && x.IsActive,
            cancellationToken);
        if (address is null)
            return new(CustomerAddressWriteStatus.AddressNotFound);

        await UnsetDefaultsAsync(customerProfileId, addressId, cancellationToken);
        address.IsDefault = true;
        address.UpdatedAt = clock.GetUtcNow();
        address.UpdatedBy = userId;
        address.RowVersion++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(CustomerAddressWriteStatus.Success, Map(address));
    }

    private Task<bool> OwnedProfileExistsAsync(Guid profileId, Guid userId, CancellationToken cancellationToken) =>
        db.CustomerProfiles.AsNoTracking().AnyAsync(
            x => x.Id == profileId && x.UserId == userId && x.IsActive,
            cancellationToken);

    private async Task<bool> LockOwnedProfileAsync(Guid profileId, Guid userId, CancellationToken cancellationToken)
    {
        var profile = await db.CustomerProfiles
            .FromSqlInterpolated($"SELECT * FROM public.customer_profiles WHERE id = {profileId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        return profile is not null && profile.UserId == userId && profile.IsActive;
    }

    private Task<int> UnsetDefaultsAsync(Guid profileId, Guid? exceptAddressId, CancellationToken cancellationToken) =>
        db.CustomerAddresses
            .Where(x => x.CustomerProfileId == profileId && x.IsActive && x.IsDefault &&
                (!exceptAddressId.HasValue || x.Id != exceptAddressId.Value))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.IsDefault, false)
                .SetProperty(x => x.UpdatedAt, clock.GetUtcNow())
                .SetProperty(x => x.RowVersion, x => x.RowVersion + 1), cancellationToken);

    private static void Apply(CustomerAddress address, UpsertCustomerAddressRequest request)
    {
        address.AddressName = Clean(request.AddressName);
        address.AddressType = request.AddressType;
        address.BuildingNo = Clean(request.BuildingNo);
        address.StreetNo = Clean(request.StreetNo);
        address.UnitNumber = Clean(request.UnitNumber);
        address.ZoneNo = Clean(request.ZoneNo);
        address.Area = request.Area.Trim();
        address.Directions = Clean(request.Directions);
        address.Latitude = request.Latitude;
        address.Longitude = request.Longitude;
        address.FormattedAddress = Clean(request.FormattedAddress);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CustomerAddressResponse Map(CustomerAddress address) => new(
        address.Id, address.CustomerProfileId, address.AddressName, address.AddressType,
        address.BuildingNo, address.StreetNo, address.UnitNumber, address.ZoneNo,
        address.Area, address.Directions, address.Latitude, address.Longitude,
        address.FormattedAddress, address.IsDefault);
}

public sealed class DeliveryTimeSlotService(DietTimeDbContext db) : IDeliveryTimeSlotService
{
    public async Task<IReadOnlyList<DeliveryTimeSlotResponse>> GetActiveAsync(CancellationToken cancellationToken) =>
        await db.DeliveryTimeSlots.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Code)
            .Select(x => new DeliveryTimeSlotResponse(x.Id, x.Code, x.Name, x.NameAr, x.StartTime, x.EndTime))
            .ToListAsync(cancellationToken);
}
