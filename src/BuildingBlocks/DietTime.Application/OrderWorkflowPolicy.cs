using System.Linq.Expressions;
using DietTime.Domain;

namespace DietTime.Application;

public static class OrderWorkflowPolicy
{
    public static readonly Expression<Func<Order, bool>> FulfillmentFilter = order =>
        (order.Status == OrderStatuses.Confirmed || order.Status == OrderStatuses.Active) &&
        order.FulfillmentBlockedReason == null &&
        (!order.RequiresPrepayment || order.PaymentStatus == PaymentStatuses.Paid) &&
        order.PaymentStatus != PaymentStatuses.Failed && order.PaymentStatus != PaymentStatuses.RefundPending &&
        order.PaymentStatus != PaymentStatuses.Refunded && order.PaymentStatus != "PARTIALLY_REFUNDED";

    public static bool CanFulfill(Order order) => (order.Status is "CONFIRMED" or "ACTIVE") &&
        order.FulfillmentBlockedReason == null &&
        (!order.RequiresPrepayment || order.PaymentStatus == PaymentStatuses.Paid) &&
        order.PaymentStatus is not ("FAILED" or "REFUND_PENDING" or "REFUNDED" or "PARTIALLY_REFUNDED");

    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        ("PENDING_APPROVAL" or "PENDING", "CONFIRMED" or "REJECTED" or "ON_HOLD" or "CANCELLED") => true,
        ("ON_HOLD", "PENDING_APPROVAL" or "CANCELLED") => true,
        ("CONFIRMED", "ACTIVE" or "CANCELLED") => true,
        ("ACTIVE", "COMPLETED") => true,
        _ => false
    };

    public static bool CanChangePayment(string from, string to) => from != to && (from, to) switch
    {
        ("UNPAID" or "PENDING" or "FAILED", "PENDING" or "PAID" or "FAILED" or "UNPAID") => true,
        ("PAID", "REFUND_PENDING" or "FAILED") => true,
        ("REFUND_PENDING", "REFUNDED" or "PARTIALLY_REFUNDED") => true,
        ("PARTIALLY_REFUNDED", "REFUNDED") => true,
        _ => false
    };
}
