using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Api;

namespace Notifications.Tests;

/// <summary>
/// La API de consulta con el consumidor de Kafka apagado por configuracion.
/// Es el ejemplo mas claro de por que las opciones deben ser configurables:
/// permite probar el servicio sin levantar un broker.
/// </summary>
public sealed class NotificationsApiFactory : WebApplicationFactory<NotificationsApiMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Kafka:Enabled", "false");
    }
}

public sealed class NotificationsApiTests(NotificationsApiFactory factory)
    : IClassFixture<NotificationsApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Health_reports_the_service_name()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("notifications");
    }

    [Fact]
    public async Task Notifications_ingested_through_the_port_are_exposed_by_the_api()
    {
        var customerId = Guid.NewGuid();
        var store = factory.Services.GetRequiredService<INotificationStore>();
        await store.AddAsync(new Notification(
            Guid.NewGuid(), Guid.NewGuid(), customerId, "rental.confirmed", "Your rental is confirmed.", DateTimeOffset.UtcNow));

        var notifications = await _client.GetFromJsonAsync<List<Notification>>($"/api/notifications?customerId={customerId}");

        notifications.ShouldNotBeNull().ShouldHaveSingleItem().Message.ShouldBe("Your rental is confirmed.");
    }

    [Fact]
    public async Task Filtering_by_an_unknown_customer_returns_an_empty_list()
    {
        var notifications = await _client.GetFromJsonAsync<List<Notification>>($"/api/notifications?customerId={Guid.NewGuid()}");

        notifications.ShouldNotBeNull().ShouldBeEmpty();
    }
}
