using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Preparation_uses_customer_names_saved_addresses_and_translated_dietary_labels_before_finalization()
    {
        var profile = await db.CustomerProfiles.SingleAsync(p => p.Id == order.CustomerProfileId);
        profile.PreferredName = "Preferred customer";
        db.UserProfiles.Add(new UserProfile { Id = Guid.NewGuid(), UserId = userId, FirstName = "Biju", LastName = "Manayagath", IsCustomer = true });
        db.CustomerProfilePreferences.Add(new CustomerProfilePreference { Id = Guid.NewGuid(), CustomerProfileId = profile.Id,
            PreferenceCode = "HIGH_PROTEIN", PreferenceType = "DIET_STYLE", PreferencePriority = 5 });
        var allergen = new Allergen { Id = Guid.NewGuid(), Code = "EGGS", IsActive = true };
        allergen.Translations.Add(new AllergenTranslation { Id = Guid.NewGuid(), LanguageCode = "en", Name = "Hen eggs" });
        db.Allergens.Add(allergen);
        db.CustomerProfileAllergens.Add(new CustomerProfileAllergen { Id = Guid.NewGuid(), CustomerProfileId = profile.Id, AllergenId = allergen.Id });
        order.DeliveryFormattedAddress = "Building 10, Street 20, Zone 30, Doha";
        order.DeliveryDirections = "Reception desk";
        await db.SaveChangesAsync();
        var menu = await PrepareAsync();
        var service = new AdminDeliveryCalendarService(db, Options.Create(new MenuRotationOptions()), clock);
        var response = await service.GetPreparationSummaryAsync(menu.DeliveryDate, default);
        var selection = Assert.Single(response.Selections);
        Assert.Equal("UNFINALIZED", selection.FinalizationStatus);
        Assert.Equal("Biju Manayagath", selection.CustomerName);
        Assert.Equal("Building 10, Street 20, Zone 30, Doha · Reception desk", selection.DeliveryAddress);
        Assert.Contains("High Protein", selection.DietaryRestrictionLabels);
        Assert.Contains("Allergen: Hen eggs", selection.DietaryRestrictionLabels);
        Assert.Contains("HIGH_PROTEIN", selection.DietaryRestrictions); // Existing machine-readable contract is retained.
        Assert.Contains("ALLERGEN: EGGS", selection.DietaryRestrictions);

        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        var json = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/admin/delivery-calendar/{menu.DeliveryDate:yyyy-MM-dd}/preparation-summary");
        Assert.Equal("Biju Manayagath", json.GetProperty("data").GetProperty("selections")[0].GetProperty("customerName").GetString());

        db.UserProfiles.Remove(await db.UserProfiles.SingleAsync(p => p.UserId == userId));
        order.FulfillmentMethod = FulfillmentMethods.Takeaway;
        order.TakeawayBranchName = "Diet Time test branch";
        await db.SaveChangesAsync();
        response = await service.GetPreparationSummaryAsync(menu.DeliveryDate, default);
        selection = Assert.Single(response.Selections);
        Assert.Equal("Preferred customer", selection.CustomerName);
        Assert.Null(selection.DeliveryAddress);
        Assert.Equal("Diet Time test branch", selection.TakeawayBranchName);
        profile.PreferredName = null;
        await db.SaveChangesAsync();
        Assert.Equal("Name not recorded", Assert.Single((await service.GetPreparationSummaryAsync(menu.DeliveryDate, default)).Selections).CustomerName);
    }

    [UpcomingPostgresFact]
    public async Task Calendar_and_preparation_reconstruct_missing_formatted_addresses_from_order_snapshots()
    {
        order.DeliveryFormattedAddress = null;
        order.DeliveryAddressName = "Home";
        order.DeliveryBuildingNo = "10";
        order.DeliveryStreetNo = "20";
        order.DeliveryUnitNumber = "4";
        order.DeliveryZoneNo = "30";
        order.DeliveryArea = "Doha";
        await db.SaveChangesAsync();
        var menu = await PrepareAsync();
        var service = new AdminDeliveryCalendarService(db, Options.Create(new MenuRotationOptions()), clock);
        var expected = "Home, Building 10, Street 20, Unit 4, Zone 30, Doha";
        Assert.Equal(expected, Assert.Single((await service.GetPreparationSummaryAsync(menu.DeliveryDate, default)).Selections).DeliveryAddress);
        var calendar = await service.GetMonthAsync(menu.DeliveryDate, menu.DeliveryDate, null, null, default);
        Assert.Equal(expected, Assert.Single(Assert.Single(calendar.Days).Orders).DeliveryAddress);
        order.DeliveryAddressName = null;
        order.DeliveryBuildingNo = null;
        order.DeliveryStreetNo = null;
        order.DeliveryUnitNumber = null;
        order.DeliveryZoneNo = null;
        order.DeliveryArea = "";
        await db.SaveChangesAsync();
        Assert.Null(Assert.Single((await service.GetPreparationSummaryAsync(menu.DeliveryDate, default)).Selections).DeliveryAddress);
    }
}
