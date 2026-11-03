using DietTime.Domain;

namespace DietTime.Application;

public sealed record PreparationLocation(string? Address, decimal? Latitude, decimal? Longitude, bool RequiresVerification = true);

public static class PreparationOrderLocation
{
    public static bool IsGeneric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var label = string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return label == "qatar" || label.StartsWith("pinnedlocation", StringComparison.Ordinal) ||
            label.StartsWith("selectedlocation", StringComparison.Ordinal);
    }

    public static bool HasSnapshot(Order order) =>
        new[] { order.DeliveryAddressName, order.DeliveryAddressType, order.DeliveryBuildingNo,
            order.DeliveryStreetNo, order.DeliveryUnitNumber, order.DeliveryZoneNo, order.DeliveryArea,
            order.DeliveryDirections, order.DeliveryFormattedAddress }.Any(s => !string.IsNullOrWhiteSpace(s)) ||
        order.DeliveryLatitude.HasValue || order.DeliveryLongitude.HasValue;

    public static PreparationLocation Resolve(Order order, CustomerAddress? linkedAddress = null)
    {
        if (order.FulfillmentMethod == FulfillmentMethods.Takeaway) return new(null, null, null);
        // A partial historical snapshot must never be filled from a customer's newer address.
        if (HasSnapshot(order))
            return Build(order.DeliveryFormattedAddress, order.DeliveryAddressName, order.DeliveryBuildingNo,
                order.DeliveryStreetNo, order.DeliveryUnitNumber, order.DeliveryZoneNo, order.DeliveryArea,
                order.DeliveryDirections, order.DeliveryLatitude, order.DeliveryLongitude);
        if (linkedAddress != null && linkedAddress.Id == order.CustomerAddressId &&
            linkedAddress.CustomerProfileId == order.CustomerProfileId)
            return Build(linkedAddress.FormattedAddress, linkedAddress.AddressName, linkedAddress.BuildingNo,
                linkedAddress.StreetNo, linkedAddress.UnitNumber, linkedAddress.ZoneNo, linkedAddress.Area,
                linkedAddress.Directions, linkedAddress.Latitude, linkedAddress.Longitude);
        return new(null, null, null);
    }

    private static PreparationLocation Build(string? formatted, string? name, string? building, string? street,
        string? unit, string? zone, string? area, string? directions, decimal? latitude, decimal? longitude)
    {
        var parts = new List<string>();
        if (!IsGeneric(formatted)) parts.Add(formatted!.Trim());
        if (!IsGeneric(name) && !string.Equals(name?.Trim(), formatted?.Trim(), StringComparison.OrdinalIgnoreCase)) parts.Add(name!.Trim());
        foreach (var (label, value) in new[] { ("Building", building), ("Street", street), ("Unit", unit), ("Zone", zone), ("Area", area), ("Landmark / directions", directions) })
            if (!IsGeneric(value)) parts.Add($"{label}: {value!.Trim()}");
        // Coordinates and generic map labels alone are not a usable delivery address.
        var usable = !IsGeneric(formatted) || !string.IsNullOrWhiteSpace(building) || !string.IsNullOrWhiteSpace(street);
        return new(parts.Count > 0 ? string.Join("; ", parts) : null, latitude, longitude, !usable);
    }
}
