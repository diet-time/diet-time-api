namespace DietTime.Domain;

// Reuses the existing subscription tables. OrderId bridges the current purchase
// flow without changing legacy subscription identifiers or historical orders.
public sealed class SubscriptionServiceDay : Entity
{
    public Guid SubscriptionId { get; set; }
    public Guid? OrderId { get; set; }
    public Order? Order { get; set; }
    public DateOnly ServiceDate { get; set; }
    public short DayOfWeek { get; set; }
    public string Status { get; set; } = "PLANNED";
    public string SelectionStatus { get; set; } = "PENDING";
    public bool SelectionLocked { get; set; }
    public DateTimeOffset? SelectionCutoffAt { get; set; }
    public int? ServiceSequence { get; set; }
    public int? MenuWeekNumber { get; set; }
    public Guid? MealPlanTemplateId { get; set; }
    public int? MealCount { get; set; }
    public int? SnackCount { get; set; }
    public string? ReviewReason { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public ICollection<CustomerMealSelection> Selections { get; set; } = [];
}

public sealed class CustomerMealSelection : Entity
{
    public Guid SubscriptionServiceDayId { get; set; }
    public SubscriptionServiceDay ServiceDay { get; set; } = null!;
    public Guid MealTypeId { get; set; }
    public Guid MenuItemId { get; set; }
    public Guid? MealPlanSlotOptionId { get; set; }
    public string? Category { get; set; }
    public int? SelectionNumber { get; set; }
    public bool IsDefaultSelection { get; set; }
    public string SelectionSource { get; set; } = "DEFAULT";
    public string Status { get; set; } = "SELECTED";
    public DateTimeOffset SelectedAt { get; set; }
    public DateTimeOffset? LastChangedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public sealed class CustomerMealSelectionHistory : Entity
{
    public Guid CustomerMealSelectionId { get; set; }
    public Guid SubscriptionServiceDayId { get; set; }
    public Guid MealTypeId { get; set; }
    public Guid? PreviousMenuItemId { get; set; }
    public Guid NewMenuItemId { get; set; }
    public string ChangeSource { get; set; } = "CUSTOMER";
    public DateTimeOffset ChangedAt { get; set; }
    public Guid? ChangedBy { get; set; }
}
