using DietTime.Application;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task FourFixes_profile_confirmation_and_retry_use_owned_profile_without_duplicates()
    {
        await using var factory = new ApiFactory(connection, services => services.AddSingleton<TimeProvider>(clock));
        using var guest = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PutAsJsonAsync("/api/v1/customer/profile", new {})).StatusCode);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var body = new { genderCode="FEMALE", dateOfBirth="1995-01-01", heightCm=165, weightKg=65,
            goalCode="LOSE_WEIGHT", dailyRoutineCode="OFFICE_WORK", activityLevelCode="MOSTLY_SITTING",
            preferredLanguage="ar", onboardingStatus="COMPLETED", preferencesConfirmed=true, allergensConfirmed=true,
            preferences=Array.Empty<object>(), allergens=Array.Empty<object>() };
        var allergen = new Allergen { Id=Guid.NewGuid(), Code="PEANUT", IsActive=true };
        db.Add(allergen); await db.SaveChangesAsync();
        var invalid = await client.PutAsJsonAsync("/api/v1/customer/profile", body with { allergens=new object[]{new {allergenId=Guid.NewGuid()}} });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        for(var attempt=0;attempt<2;attempt++) {
            var withAllergen = await client.PutAsJsonAsync("/api/v1/customer/profile", body with { allergens=new object[]{new {allergenId=allergen.Id}} });
            Assert.True(withAllergen.IsSuccessStatusCode, await withAllergen.Content.ReadAsStringAsync());
        }
        Assert.Equal(1, await db.CustomerProfileAllergens.CountAsync());
        for(var attempt=0;attempt<2;attempt++) {
            var response = await client.PutAsJsonAsync("/api/v1/customer/profile", body);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }
        var fetched = await client.GetFromJsonAsync<JsonElement>("/api/v1/customer/profile");
        var data = fetched.GetProperty("data");
        Assert.Equal(order.CustomerProfileId, data.GetProperty("id").GetGuid());
        Assert.True(data.GetProperty("allergensConfirmed").GetBoolean());
        Assert.True(data.GetProperty("preferencesConfirmed").GetBoolean());
        Assert.Empty(data.GetProperty("allergens").EnumerateArray());
        Assert.Equal(1, await db.CustomerProfiles.CountAsync());
        Assert.Empty(await db.CustomerProfileAllergens.ToListAsync());
    }

    [UpcomingPostgresFact]
    public async Task FourFixes_address_slot_persists_and_legacy_edit_retains_choice()
    {
        var slot = new DeliveryTimeSlot { Id=Guid.NewGuid(), Code="MORNING", Name="Morning", NameAr="Morning", StartTime=new(7,0), EndTime=new(11,0), IsActive=true };
        db.Add(slot); await db.SaveChangesAsync();
        await using var factory = new ApiFactory(connection, services => services.AddSingleton<TimeProvider>(clock));
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Development-User-Id", userId.ToString());
        var path = $"/api/v1/customer-profiles/{order.CustomerProfileId}/addresses";
        var body = new UpsertCustomerAddressRequest("Home", "HOME", "12", "34", null, "56", "Doha", null, 25.2m,51.5m,"Doha", true, slot.Id);
        var missing = await client.PostAsJsonAsync(path, body with { DeliveryTimeSlotId=null });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var created = await client.PostAsJsonAsync(path, body);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var saved = await created.Content.ReadFromJsonAsync<CustomerAddressResponse>();
        Assert.Equal(slot.Id, saved!.DeliveryTimeSlotId);
        var edited = await client.PutAsJsonAsync($"{path}/{saved.Id}", body with { Area="Al Wakrah", DeliveryTimeSlotId=null });
        Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync());
        Assert.Equal(slot.Id, (await edited.Content.ReadFromJsonAsync<CustomerAddressResponse>())!.DeliveryTimeSlotId);
        var fetched = await client.GetFromJsonAsync<CustomerAddressListResponse>(path);
        Assert.Equal(slot.Id, fetched!.Items.Single().DeliveryTimeSlotId);
        Assert.Equal(new TimeOnly(9,0), (await db.Orders.AsNoTracking().SingleAsync()).DeliveryStartTime);
    }

    [UpcomingPostgresFact]
    public async Task FourFixes_Qatar_today_rejected_even_with_zero_lead_and_tomorrow_accepted()
    {
        clock.Now = new DateTimeOffset(2026,10,25,22,0,0,TimeSpan.Zero); // 26 October in Qatar
        var slot=new DeliveryTimeSlot {Id=Guid.NewGuid(),Code="MORNING",Name="Morning",NameAr="Morning",StartTime=new(7,0),EndTime=new(11,0),IsActive=true};
        var address=new CustomerAddress {Id=Guid.NewGuid(),CustomerProfileId=order.CustomerProfileId,Area="Doha",IsActive=true,DeliveryTimeSlotId=slot.Id};
        db.AddRange(slot,address); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE SEQUENCE IF NOT EXISTS public.order_number_seq");
        await using var factory=new ApiFactory(connection,services=>{
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<OrderOptions>(o=>o.MinimumLeadTimeDays=0);
            services.PostConfigure<DietTime.Infrastructure.TwilioWhatsAppOptions>(o=>o.Enabled=false);
        });
        using var client=factory.CreateClient();client.DefaultRequestHeaders.Add("X-Development-User-Id",userId.ToString());
        var body=new PlaceOrderRequest(order.CustomerProfileId,order.MealPlanTemplateId,order.MealPlanPriceId,address.Id,slot.Id,
            new(2026,10,26),[1,2,3,4,6,7],order.Meals.Select(m=>new PlaceOrderMealRequest(m.MealTypeId,m.Quantity)).ToArray());
        async Task<HttpResponseMessage> Post(PlaceOrderRequest request) {
            using var message=new HttpRequestMessage(HttpMethod.Post,"/api/v1/orders"){Content=JsonContent.Create(request)};
            message.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString());return await client.SendAsync(message);
        }
        Assert.Equal(HttpStatusCode.BadRequest,(await Post(body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Post(body with {StartDate=new(2026,10,30)})).StatusCode);
        var accepted=await Post(body with {StartDate=new(2026,10,27)});
        Assert.True(accepted.StatusCode==HttpStatusCode.Created,await accepted.Content.ReadAsStringAsync());
        var response=await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("07:00:00",response.GetProperty("delivery").GetProperty("timeSlot").GetProperty("startTime").GetString());
        var options=await client.GetFromJsonAsync<JsonElement>($"/api/v1/customer/meal-plans/{order.MealPlanTemplateId}/purchase-options");
        var package=options.GetProperty("data").GetProperty("mealConfigurations")[0].GetProperty("packages")[0];
        Assert.Equal("2026-10-27",package.GetProperty("earliestStartDate").GetString());
        Assert.Equal(1,package.GetProperty("startDateLeadTimeDays").GetInt32());
    }

    [UpcomingPostgresFact]
    public async Task FourFixes_slot_migration_repeatable_without_changing_orders_or_existing_addresses()
    {
        db.AddRange(new DeliveryTimeSlot {Id=Guid.NewGuid(),Code="MORNING",Name="Morning",NameAr="Morning",StartTime=new(9,0),EndTime=new(11,0),IsActive=true},
            new DeliveryTimeSlot {Id=Guid.NewGuid(),Code="EVENING",Name="Evening",NameAr="Evening",StartTime=new(19,0),EndTime=new(20,15),IsActive=true});
        db.Add(new CustomerAddress {Id=Guid.NewGuid(),CustomerProfileId=order.CustomerProfileId,Area="Doha",IsActive=true});
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE public.customer_addresses DROP COLUMN delivery_time_slot_id CASCADE");
        var sql=await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../database/scripts/20261026_address_delivery_slots.sql")));
        await db.Database.ExecuteSqlRawAsync(sql);await db.Database.ExecuteSqlRawAsync(sql);
        var slots=await db.DeliveryTimeSlots.AsNoTracking().OrderBy(s=>s.StartTime).ToArrayAsync();
        Assert.Equal(new TimeOnly(7,0),slots[0].StartTime);Assert.Equal(new TimeOnly(16,0),slots[1].StartTime);Assert.Equal(new TimeOnly(18,0),slots[1].EndTime);
        Assert.Null((await db.CustomerAddresses.AsNoTracking().SingleAsync()).DeliveryTimeSlotId);
        var historic=await db.Orders.AsNoTracking().SingleAsync();Assert.Equal(new TimeOnly(9,0),historic.DeliveryStartTime);Assert.Equal(order.StartDate,historic.StartDate);
    }
}
