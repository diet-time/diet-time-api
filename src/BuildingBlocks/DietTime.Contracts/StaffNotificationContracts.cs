namespace DietTime.Contracts;
public sealed record StaffNotificationResponse(Guid Id,string EventType,string Title,string Message,Guid OrderId,string OrderNumber,Guid? ServiceDayId,DateOnly? ServiceDate,DateTimeOffset CreatedAt,DateTimeOffset? ReadAt,string TargetUrl,string? CustomerName = null);
public sealed record StaffNotificationListResponse(IReadOnlyList<StaffNotificationResponse> Items,int Page,int PageSize,int TotalCount);
public sealed record StaffNotificationCountResponse(int UnreadCount);
