using DietTime.Infrastructure;
using DietTime.Meal.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class TwilioAdminActionsTests
{
    [Fact]
    public async Task Admin_actions_are_disabled_by_default_even_when_notifications_are_enabled()
    {
        var settings = new TwilioWhatsAppOptions { Enabled = true };
        Assert.False(settings.AdminActionsEnabled);
        // No request or business service is needed: disabled actions stop before reading a reply.
        var controller = new TwilioWebhookController(Options.Create(settings), null!);
        Assert.IsType<NotFoundResult>(await controller.Inbound(default));
        Assert.True(settings.Enabled);
    }
}
