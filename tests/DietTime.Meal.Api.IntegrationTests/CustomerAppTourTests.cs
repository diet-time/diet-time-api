using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task App_tour_preference_is_owned_by_authenticated_customer_and_preserves_profile()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var client = setup.Client;
        var otherUser = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = otherUser, UserName = "tour-other", SecurityStamp = Guid.NewGuid().ToString() });
        var otherProfile = new CustomerProfile { Id = Guid.NewGuid(), UserId = otherUser, IsActive = true, PreferredLanguage = "en" };
        db.CustomerProfiles.Add(otherProfile);
        await db.SaveChangesAsync();
        var original = await db.CustomerProfiles.AsNoTracking().SingleAsync(p => p.UserId == userId);

        var read = await client.GetFromJsonAsync<JsonElement>("/api/v1/customer/profile");
        Assert.False(read.GetProperty("data").GetProperty("isAppTourEnabled").GetBoolean());
        var missing = await client.PatchAsJsonAsync("/api/v1/customer/profile/app-tour", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        // An extra customer ID is ignored: identity always comes from the authenticated principal.
        var enabled = await client.PatchAsJsonAsync("/api/v1/customer/profile/app-tour", new { isAppTourEnabled = true, customerId = otherProfile.Id });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.True((await enabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("isAppTourEnabled").GetBoolean());
        db.ChangeTracker.Clear();
        var saved = await db.CustomerProfiles.AsNoTracking().SingleAsync(p => p.UserId == userId);
        Assert.True(saved.IsAppTourEnabled);
        Assert.Equal(original.GoalCode, saved.GoalCode);
        Assert.Equal(original.WeightKg, saved.WeightKg);
        Assert.Equal(original.PreferredLanguage, saved.PreferredLanguage);
        Assert.Equal(original.RowVersion + 1, saved.RowVersion);
        Assert.False((await db.CustomerProfiles.AsNoTracking().SingleAsync(p => p.UserId == otherUser)).IsAppTourEnabled);
        var disabled = await client.PatchAsJsonAsync("/api/v1/customer/profile/app-tour", new { isAppTourEnabled = false });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        read = await client.GetFromJsonAsync<JsonElement>("/api/v1/customer/profile");
        Assert.False(read.GetProperty("data").GetProperty("isAppTourEnabled").GetBoolean());

        client.DefaultRequestHeaders.Remove("X-Development-User-Id");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PatchAsJsonAsync("/api/v1/customer/profile/app-tour", new { isAppTourEnabled = true })).StatusCode);
    }

    [UpcomingPostgresFact]
    public async Task App_tour_new_profiles_opt_in_once_and_unrelated_updates_do_not_reset_preference()
    {
        var setup = await ApprovalClient();
        await using var factory = setup.Factory;
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICustomerProfileService>();
        var newUser = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = newUser, UserName = "tour-new", SecurityStamp = Guid.NewGuid().ToString() });
        await db.SaveChangesAsync();
        var profile = await service.UpdatePreferredNameAsync(newUser, "New customer", default);
        Assert.True(profile!.IsAppTourEnabled);
        await service.UpdateAppTourAsync(newUser, false, default);
        profile = await service.UpdatePreferredNameAsync(newUser, "Renamed customer", default);
        Assert.False(profile!.IsAppTourEnabled);
        var result = await service.UpsertAsync(newUser, new UpsertCustomerProfileRequest { PreferredLanguage = "en", OnboardingStatus = "IN_PROGRESS" }, default);
        Assert.False((await service.GetAsync(newUser, default))!.IsAppTourEnabled);
        Assert.NotNull(result);
        var viaQuestionnaire = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = viaQuestionnaire, UserName = "tour-questionnaire", SecurityStamp = Guid.NewGuid().ToString() });
        await db.SaveChangesAsync();
        await service.UpsertAsync(viaQuestionnaire, new UpsertCustomerProfileRequest { PreferredLanguage = "en", OnboardingStatus = "IN_PROGRESS" }, default);
        Assert.True((await service.GetAsync(viaQuestionnaire, default))!.IsAppTourEnabled);
        Assert.Null(await service.UpdateAppTourAsync(Guid.NewGuid(), true, default));
        var inactive = await db.CustomerProfiles.SingleAsync(p => p.UserId == newUser);
        inactive.IsActive = false;
        await db.SaveChangesAsync();
        using var nextRequest = factory.Services.CreateScope();
        Assert.Null(await nextRequest.ServiceProvider.GetRequiredService<ICustomerProfileService>().UpdateAppTourAsync(newUser, true, default));
    }

}
