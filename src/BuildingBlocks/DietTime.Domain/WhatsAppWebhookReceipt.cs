namespace DietTime.Domain;
public sealed class WhatsAppWebhookReceipt : Entity
{
    public string MessageSid { get; set; } = "";
    public Guid OrderId { get; set; }
    public Guid ActorId { get; set; }
    public string Action { get; set; } = "";
    public string Outcome { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
