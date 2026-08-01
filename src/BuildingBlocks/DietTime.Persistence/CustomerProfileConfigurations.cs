using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DietTime.Persistence;

public sealed class CustomerProfileConfiguration : IEntityTypeConfiguration<CustomerProfile>
{
    public void Configure(EntityTypeBuilder<CustomerProfile> entity)
    {
        entity.ToTable("customer_profiles", "public");
        entity.HasKey(x => x.Id);

        entity.Property(x => x.Id).HasColumnName("id");
        entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired(false);
        entity.Property(x => x.PreferredName).HasColumnName("preferred_name").HasMaxLength(100);
        entity.Property(x => x.GenderCode).HasColumnName("gender_code").HasMaxLength(30);
        entity.Property(x => x.DateOfBirth).HasColumnName("date_of_birth");
        entity.Property(x => x.HeightCm).HasColumnName("height_cm").HasPrecision(6, 2);
        entity.Property(x => x.WeightKg).HasColumnName("weight_kg").HasPrecision(6, 2);
        entity.Property(x => x.Bmi).HasColumnName("bmi").HasPrecision(6, 2);
        entity.Property(x => x.BmiCategoryCode).HasColumnName("bmi_category_code").HasMaxLength(30);
        entity.Property(x => x.GoalCode).HasColumnName("goal_code").HasMaxLength(50);
        entity.Property(x => x.DailyRoutineCode).HasColumnName("daily_routine_code").HasMaxLength(50);
        entity.Property(x => x.ActivityLevelCode).HasColumnName("activity_level_code").HasMaxLength(50);
        entity.Property(x => x.PreferredLanguage)
            .HasColumnName("preferred_language")
            .HasMaxLength(10)
            .HasDefaultValue("en")
            .IsRequired();
        entity.Property(x => x.OnboardingStatus)
            .HasColumnName("onboarding_status")
            .HasMaxLength(30)
            .HasDefaultValue("NOT_STARTED")
            .IsRequired();
        entity.Property(x => x.OnboardingCompletedAt).HasColumnName("onboarding_completed_at");
        entity.Property(x => x.AllergensConfirmed)
            .HasColumnName("allergens_confirmed")
            .HasDefaultValue(false);
        entity.Property(x => x.PreferencesConfirmed)
            .HasColumnName("preferences_confirmed")
            .HasDefaultValue(false);
        entity.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        entity.Property(x => x.CreatedBy).HasColumnName("created_by");
        entity.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        entity.Property(x => x.RowVersion)
            .HasColumnName("row_version")
            .HasDefaultValue(1L)
            .IsConcurrencyToken();

        entity.HasOne<ApplicationUser>()
            .WithOne(x => x.CustomerProfile)
            .HasForeignKey<CustomerProfile>(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(x => x.UserId)
            .IsUnique()
            .HasFilter("user_id IS NOT NULL")
            .HasDatabaseName("ux_customer_profiles_user");
        entity.HasIndex(x => x.GoalCode).HasDatabaseName("ix_customer_profiles_goal_code");
        entity.HasIndex(x => x.ActivityLevelCode).HasDatabaseName("ix_customer_profiles_activity_level_code");
        entity.HasIndex(x => x.OnboardingStatus).HasDatabaseName("ix_customer_profiles_onboarding_status");
        entity.HasIndex(x => x.IsActive).HasDatabaseName("ix_customer_profiles_is_active");
    }
}

public sealed class CustomerNutritionTargetConfiguration
    : IEntityTypeConfiguration<CustomerNutritionTarget>
{
    public void Configure(EntityTypeBuilder<CustomerNutritionTarget> entity)
    {
        entity.ToTable("customer_nutrition_targets", "public");
        entity.HasKey(x => x.Id);

        entity.Property(x => x.Id).HasColumnName("id");
        entity.Property(x => x.CustomerProfileId).HasColumnName("customer_profile_id");
        entity.Property(x => x.DailyCaloriesKcal).HasColumnName("daily_calories_kcal");
        entity.Property(x => x.DailyProteinG).HasColumnName("daily_protein_g").HasPrecision(8, 2);
        entity.Property(x => x.DailyCarbohydratesG).HasColumnName("daily_carbohydrates_g").HasPrecision(8, 2);
        entity.Property(x => x.DailyFatG).HasColumnName("daily_fat_g").HasPrecision(8, 2);
        entity.Property(x => x.DailyFiberG).HasColumnName("daily_fiber_g").HasPrecision(8, 2);
        entity.Property(x => x.DailyWaterMl).HasColumnName("daily_water_ml");
        entity.Property(x => x.CalculationMethod).HasColumnName("calculation_method").HasMaxLength(50);
        entity.Property(x => x.CalculationVersion).HasColumnName("calculation_version").HasMaxLength(30);
        entity.Property(x => x.CalculatedAt).HasColumnName("calculated_at");
        entity.Property(x => x.IsCurrent).HasColumnName("is_current");
        entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        entity.Property(x => x.CreatedBy).HasColumnName("created_by");
        entity.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        entity.Property(x => x.RowVersion)
            .HasColumnName("row_version")
            .HasDefaultValue(1L)
            .IsConcurrencyToken();

        entity.HasOne(x => x.CustomerProfile)
            .WithMany(x => x.NutritionTargets)
            .HasForeignKey(x => x.CustomerProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(x => x.CustomerProfileId)
            .HasDatabaseName("ix_customer_nutrition_targets_profile");
        entity.HasIndex(x => x.CustomerProfileId)
            .IsUnique()
            .HasFilter("is_current = TRUE")
            .HasDatabaseName("ux_customer_nutrition_targets_current");
    }
}

public sealed class CustomerProfilePreferenceConfiguration
    : IEntityTypeConfiguration<CustomerProfilePreference>
{
    public void Configure(EntityTypeBuilder<CustomerProfilePreference> entity)
    {
        entity.ToTable("customer_profile_preferences", "public");
        entity.HasKey(x => x.Id);

        entity.Property(x => x.Id).HasColumnName("id");
        entity.Property(x => x.CustomerProfileId).HasColumnName("customer_profile_id");
        entity.Property(x => x.PreferenceCode)
            .HasColumnName("preference_code")
            .HasMaxLength(50)
            .IsRequired();
        entity.Property(x => x.PreferenceType).HasColumnName("preference_type").HasMaxLength(30);
        entity.Property(x => x.PreferencePriority).HasColumnName("preference_priority").IsRequired();
        entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        entity.HasOne(x => x.CustomerProfile)
            .WithMany(x => x.Preferences)
            .HasForeignKey(x => x.CustomerProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(x => x.CustomerProfileId)
            .HasDatabaseName("ix_customer_profile_preferences_profile");
        entity.HasIndex(x => new { x.CustomerProfileId, x.PreferenceCode })
            .IsUnique()
            .HasDatabaseName("ux_customer_profile_preferences_code");
    }
}

public sealed class CustomerProfileAllergenConfiguration
    : IEntityTypeConfiguration<CustomerProfileAllergen>
{
    public void Configure(EntityTypeBuilder<CustomerProfileAllergen> entity)
    {
        entity.ToTable("customer_profile_allergens", "public");
        entity.HasKey(x => x.Id);

        entity.Property(x => x.Id).HasColumnName("id");
        entity.Property(x => x.CustomerProfileId).HasColumnName("customer_profile_id");
        entity.Property(x => x.AllergenId).HasColumnName("allergen_id");
        entity.Property(x => x.SeverityCode).HasColumnName("severity_code").HasMaxLength(30);
        entity.Property(x => x.MedicallyConfirmed).HasColumnName("medically_confirmed");
        entity.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(500);
        entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        entity.Property(x => x.CreatedBy).HasColumnName("created_by");
        entity.Property(x => x.UpdatedBy).HasColumnName("updated_by");

        entity.HasOne(x => x.CustomerProfile)
            .WithMany(x => x.Allergens)
            .HasForeignKey(x => x.CustomerProfileId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(x => x.Allergen)
            .WithMany()
            .HasForeignKey(x => x.AllergenId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(x => x.CustomerProfileId)
            .HasDatabaseName("ix_customer_profile_allergens_profile");
        entity.HasIndex(x => new { x.CustomerProfileId, x.AllergenId })
            .IsUnique()
            .HasDatabaseName("ux_customer_profile_allergens_allergen");
    }
}
