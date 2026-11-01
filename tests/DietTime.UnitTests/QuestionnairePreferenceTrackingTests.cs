using System.Reflection;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DietTime.UnitTests;

public sealed class QuestionnairePreferenceTrackingTests
{
    [Fact]
    public void New_preference_on_an_existing_profile_is_inserted_not_updated()
    {
        using var db = new DietTimeDbContext(new DbContextOptionsBuilder<DietTimeDbContext>()
            .UseNpgsql("Host=localhost;Database=tracking_only;Username=test;Password=test")
            .Options);
        var profile = new CustomerProfile { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), RowVersion = 4 };
        db.Attach(profile);
        var service = new CustomerProfileService(db, null!, TimeProvider.System,
            NullLogger<CustomerProfileService>.Instance);
        var synchronize = typeof(CustomerProfileService).GetMethod("SynchronizePreferences",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        synchronize.Invoke(service, [profile,
            new[] { new CustomerPreferenceRequest("HIGH_PROTEIN", "DIET_STYLE", 5) }, DateTimeOffset.UtcNow]);
        db.ChangeTracker.DetectChanges();
        var preference = Assert.Single(profile.Preferences);
        Assert.Equal(EntityState.Added, db.Entry(preference).State);
        Assert.Equal(profile.Id, preference.CustomerProfileId);
        Assert.True(db.Model.FindEntityType(typeof(CustomerProfile))!
            .FindProperty(nameof(CustomerProfile.RowVersion))!.IsConcurrencyToken);
    }
}