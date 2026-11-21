namespace DietTime.Meal.Api.Middleware;

public static class OtpRequestDiagnostics
{
    private static readonly object ReasonKey = new();

    public static void SetReason(HttpContext? context, string reason)
    {
        if (context != null) context.Items[ReasonKey] = reason;
    }

    public static string Summary(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) ||
            !string.Equals(context.Request.Path.Value?.TrimEnd('/'),
                "/api/v1/auth/phone-otp/request", StringComparison.OrdinalIgnoreCase)) return "";

        // Only internal reason labels and safe metadata; never inspect request/response bodies.
        var reason = context.Items.TryGetValue(ReasonKey, out var value) ? value :
            context.Response.StatusCode >= 400 ? "RejectedBeforeOtpAction" : "CompletedBeforeOtpAction";
        return $"OtpDiagnostics=v2 Reason={reason} EndpointMatched={context.GetEndpoint() != null} JsonContentType={context.Request.HasJsonContentType()} TraceId={context.TraceIdentifier}";
    }
}
