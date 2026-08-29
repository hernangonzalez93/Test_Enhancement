using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Billing.Api;
using Billing.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Billing.Api.Tests;

/// <summary>
/// La API completa en memoria con el puerto de aplicacion sustituido, igual que
/// en Rentals. La diferencia es el adaptador: aqui son controllers clasicos, asi
/// que estas pruebas cubren tambien lo que aporta `[ApiController]` (validacion
/// automatica del modelo, ProblemDetails) y el enrutado por atributos.
/// </summary>
public sealed class BillingApiFactory : WebApplicationFactory<BillingApiMarker>
{
    public IInvoiceService InvoiceService { get; } = Substitute.For<IInvoiceService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Kafka:Enabled", "false");
        builder.UseSetting("Database:AutoMigrate", "false");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IInvoiceService>();
            services.AddScoped(_ => InvoiceService);
        });
    }
}

public sealed class InvoicesControllerTests(BillingApiFactory factory) : IClassFixture<BillingApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static InvoiceDto SampleInvoice(Guid? id = null, string status = "Issued") => new(
        id ?? Guid.CreateVersion7(),
        "INV-ABCD1234",
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        status,
        "USD",
        200m,
        38m,
        238m,
        [new InvoiceLineDto("rental", 200m)],
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null);

    [Fact]
    public async Task Getting_an_invoice_returns_200_with_its_totals()
    {
        var invoice = SampleInvoice();
        factory.InvoiceService.GetAsync(invoice.Id, Arg.Any<CancellationToken>())
            .Returns(Result<InvoiceDto>.Success(invoice));

        var response = await _client.GetAsync($"/api/invoices/{invoice.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<InvoiceDto>()).ShouldNotBeNull();
        body.Total.ShouldBe(238m);
        body.Lines.ShouldHaveSingleItem().Concept.ShouldBe("rental");
    }

    [Fact]
    public async Task Getting_an_unknown_invoice_returns_404()
    {
        var id = Guid.CreateVersion7();
        factory.InvoiceService.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result<InvoiceDto>.Failure(InvoiceErrors.NotFound(id)));

        (await _client.GetAsync($"/api/invoices/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_non_guid_id_does_not_match_the_route_constraint()
    {
        (await _client.GetAsync("/api/invoices/not-a-guid")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Querying_by_rental_returns_a_list_with_the_invoice()
    {
        var invoice = SampleInvoice();
        factory.InvoiceService.GetByRentalAsync(invoice.RentalId, Arg.Any<CancellationToken>())
            .Returns(Result<InvoiceDto>.Success(invoice));

        var invoices = await _client.GetFromJsonAsync<List<InvoiceDto>>($"/api/invoices?rentalId={invoice.RentalId}");

        invoices.ShouldNotBeNull().ShouldHaveSingleItem().Number.ShouldBe("INV-ABCD1234");
    }

    [Fact]
    public async Task Querying_by_a_rental_without_invoice_returns_an_empty_list()
    {
        var rentalId = Guid.CreateVersion7();
        factory.InvoiceService.GetByRentalAsync(rentalId, Arg.Any<CancellationToken>())
            .Returns(Result<InvoiceDto>.Failure(InvoiceErrors.NotFound(rentalId)));

        var invoices = await _client.GetFromJsonAsync<List<InvoiceDto>>($"/api/invoices?rentalId={rentalId}");

        invoices.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Fact]
    public async Task Querying_without_any_filter_is_rejected_with_400()
    {
        var response = await _client.GetAsync("/api/invoices");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("customerId");
    }

    [Fact]
    public async Task Querying_by_customer_returns_the_collection()
    {
        var customerId = Guid.CreateVersion7();
        IReadOnlyList<InvoiceDto> invoices = [SampleInvoice(), SampleInvoice()];
        factory.InvoiceService.ListByCustomerAsync(customerId, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<InvoiceDto>>.Success(invoices));

        var body = await _client.GetFromJsonAsync<List<InvoiceDto>>($"/api/invoices?customerId={customerId}");

        body.ShouldNotBeNull().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Paying_an_invoice_returns_200_with_the_new_state()
    {
        var invoice = SampleInvoice(status: "Paid");
        factory.InvoiceService.PayAsync(invoice.Id, Arg.Any<CancellationToken>())
            .Returns(Result<InvoiceDto>.Success(invoice));

        var response = await _client.PostAsync($"/api/invoices/{invoice.Id}/pay", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<InvoiceDto>()).ShouldNotBeNull().Status.ShouldBe("Paid");
    }

    [Fact]
    public async Task Voiding_an_invoice_returns_200()
    {
        var invoice = SampleInvoice(status: "Void");
        factory.InvoiceService.VoidAsync(invoice.Id, Arg.Any<CancellationToken>())
            .Returns(Result<InvoiceDto>.Success(invoice));

        (await _client.PostAsync($"/api/invoices/{invoice.Id}/void", null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("invoice.not_found", HttpStatusCode.NotFound)]
    [InlineData("invoice.invalid_state", HttpStatusCode.Conflict)]
    [InlineData("invoice.payment_mismatch", HttpStatusCode.Conflict)]
    [InlineData("invoice.nothing_to_bill", HttpStatusCode.UnprocessableEntity)]
    [InlineData("money.invalid_currency", HttpStatusCode.BadRequest)]
    public async Task Each_business_error_code_maps_to_its_http_status(string code, HttpStatusCode expected)
    {
        var id = Guid.CreateVersion7();
        factory.InvoiceService.PayAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result<InvoiceDto>.Failure(code, "boom"));

        (await _client.PostAsync($"/api/invoices/{id}/pay", null)).StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task A_failure_carries_the_error_code_in_problem_details()
    {
        var id = Guid.CreateVersion7();
        factory.InvoiceService.VoidAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result<InvoiceDto>.Failure("invoice.invalid_state", "Cannot void a Paid invoice."));

        var response = await _client.PostAsync($"/api/invoices/{id}/void", null);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("errorCode").GetString().ShouldBe("invoice.invalid_state");
        document.RootElement.GetProperty("detail").GetString().ShouldBe("Cannot void a Paid invoice.");
    }

    [Fact]
    public async Task Health_reports_the_service_name()
    {
        (await (await _client.GetAsync("/health")).Content.ReadAsStringAsync()).ShouldContain("billing");
    }
}
