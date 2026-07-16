using DietTime.Application;
using DietTime.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class MealSelectionService(DietTimeDbContext db) : IMealSelectionService
{
    public async Task<MealSelectionValidationResponse> ValidateAsync(MealSelectionRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var day = await db.MealPlanTemplateDays.AsNoTracking()
            .Where(d => d.Id == request.TemplateDayId && d.MealPlanTemplateId == request.PlanId && d.IsActive && d.Plan.IsActive && d.Plan.IsPublished)
            .Select(d => new { d.Plan.ValidFrom, d.Plan.ValidUntil, Slots = d.Slots.Where(s => s.IsActive).Select(s => new { s.Id, s.MinimumSelection, s.MaximumSelection, s.AllowsPaidUpgrade, s.SelectionCutoffTime }).ToList() })
            .SingleOrDefaultAsync(ct);
        if (day is null) return Invalid("The plan or template day is unavailable.");
        var today = DateOnly.FromDateTime(now.UtcDateTime); if ((day.ValidFrom.HasValue && day.ValidFrom > today) || (day.ValidUntil.HasValue && day.ValidUntil < today)) return Invalid("The plan is outside its validity period.");
        if (request.Selections.GroupBy(x => x.SlotOptionId).Any(g => g.Count() > 1)) return Invalid("Duplicate slot options are not allowed.");

        var requestedOptionIds = request.Selections.Select(x => x.SlotOptionId).ToArray();
        var options = await db.MealPlanSlotOptions.AsNoTracking().Where(o => requestedOptionIds.Contains(o.Id))
            .Select(o => new { o.Id, o.MealPlanTemplateSlotId, o.MealItemId, o.MealVariantId, o.AdditionalPrice, o.IsAvailable, o.AvailableFrom, o.AvailableUntil, MealStatus = o.MealItem.Status, MealAvailable = o.MealItem.IsAvailable, MealFrom = o.MealItem.AvailableFrom, MealUntil = o.MealItem.AvailableUntil, VariantPrice = o.MealVariant == null ? 0 : o.MealVariant.AdditionalPrice, VariantActive = o.MealVariant == null || o.MealVariant.IsActive }).ToListAsync(ct);
        var warnings = new List<string>(); decimal total = 0;
        foreach (var selection in request.Selections)
        {
            var slot = day.Slots.SingleOrDefault(s => s.Id == selection.SlotId); var option = options.SingleOrDefault(o => o.Id == selection.SlotOptionId);
            if (slot is null || option is null || option.MealPlanTemplateSlotId != slot.Id || option.MealItemId != selection.MealItemId || option.MealVariantId != selection.MealVariantId) return Invalid("A selection does not belong to the requested plan slot.");
            if (!option.IsAvailable || option.AvailableFrom > now || option.AvailableUntil <= now || !MealAvailability.IsAvailable(option.MealStatus, option.MealAvailable, option.MealFrom, option.MealUntil, now) || !option.VariantActive) return Invalid("A selected meal or variant is unavailable.");
            if (slot.SelectionCutoffTime.HasValue && TimeOnly.FromDateTime(now.UtcDateTime) > slot.SelectionCutoffTime) return Invalid("The selection cutoff time has passed.");
            total += SelectionRules.ResolveAdditionalPrice(option.AdditionalPrice, option.VariantPrice, slot.AllowsPaidUpgrade);
        }
        foreach (var slot in day.Slots) { var count = request.Selections.Count(s => s.SlotId == slot.Id); if (!SelectionRules.IsCountValid(count, slot.MinimumSelection, slot.MaximumSelection)) return Invalid($"Slot {slot.Id} requires between {slot.MinimumSelection} and {slot.MaximumSelection} selections."); }
        warnings.Add("Allergen-profile validation is not available because the current schema has no customer dietary-profile tables.");
        return new(true, total, "QAR", warnings);
    }

    private static MealSelectionValidationResponse Invalid(string warning) => new(false, 0, "QAR", [warning]);
}

