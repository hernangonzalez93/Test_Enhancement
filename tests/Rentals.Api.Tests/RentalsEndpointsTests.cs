using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Rentals.Api.Endpoints;
using Rentals.Application.Common;
using Rentals.Application.Rentals;
using Rentals.Domain.Model;
using TestSupport;

namespace Rentals.Api.Tests;

public sealed class RentalsEndpointsTests(RentalsApiFactory factory) : IClassFixture<RentalsApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static RentalDto SampleDto(Guid? id = null, string status = "Pending") => new(
        id ?? Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        TestData.EconomyVehicleId,
        FixedClock.DefaultNow.AddDays(10),
        FixedClock.DefaultNow.AddDays(13),
        3,
        status,
        50m,
        150m,
        null,
        null,
        "USD",
        0);

    private static CreateRentalRequest ValidRequest() => new(
        Guid.CreateVersion7(),
        TestData.EconomyVehicleId,
        FixedClock.DefaultNow.AddDays(10),
        FixedClock.DefaultNow.AddDays(13),
        TestData.ValidLicense,
        FixedClock.DefaultNow.AddYears(3),
        []);

    [Fact]
    public async Task Creating_a_rental_returns_201_with_the_location_header()
    {
        var dto = SampleDto();
        factory.RentalService
            .RequestAsync(Arg.Any<RequestRentalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<RentalDto>.Success(dto));

        var response = await _client.PostAsJsonAsync("/api/rentals", ValidRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldBe($"/api/rentals/{dto.Id}");

        var body = await response.Content.ReadFromJsonAsync<RentalDto>();
        body.ShouldNotBeNull().Id.ShouldBe(dto.Id);
        body.Status.ShouldBe("Pending");
    }

    [Fact]
    public async Task The_request_body_is_translated_into_the_application_command()
    {
        factory.RentalService
            .RequestAsync(Arg.Any<RequestRentalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<RentalDto>.Success(SampleDto()));

        var request = ValidRequest();
        await _client.PostAsJsonAsync("/api/rentals", request);

        await factory.RentalService.Received().RequestAsync(
            Arg.Is<RequestRentalCommand>(command =>
                command.CustomerId == request.CustomerId
                && command.VehicleId == request.VehicleId
                && command.LicenseNumber == request.LicenseNumber),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_empty_customer_id_is_rejected_with_400_before_reaching_the_service()
    {
        var response = await _client.PostAsJsonAsync("/api/rentals", ValidRequest() with { CustomerId = Guid.Empty });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("customerId is required");
    }

    [Fact]
    public async Task An_inverted_period_is_rejected_with_400()
    {
        var request = ValidRequest();
        var response = await _client.PostAsJsonAsync(
            "/api/rentals",
            request with { PeriodEnd = request.PeriodStart.AddDays(-1) });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("periodEnd must be after periodStart");
    }

    [Theory]
    [InlineData("vehicle.not_found", HttpStatusCode.NotFound)]
    [InlineData("rental.not_found", HttpStatusCode.NotFound)]
    [InlineData("vehicle.unavailable", HttpStatusCode.Conflict)]
    [InlineData("rental.overlapping", HttpStatusCode.Conflict)]
    [InlineData("rental.invalid_state", HttpStatusCode.Conflict)]
    [InlineData("rental.not_startable_yet", HttpStatusCode.Conflict)]
    [InlineData("pricing.unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("fleet.unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("rental.invalid_period", HttpStatusCode.BadRequest)]
    [InlineData("rental.license_expired", HttpStatusCode.BadRequest)]
    public async Task Each_business_error_code_maps_to_its_http_status(string errorCode, HttpStatusCode expected)
    {
        factory.RentalService
            .RequestAsync(Arg.Any<RequestRentalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<RentalDto>.Failure(errorCode, "boom"));

        var response = await _client.PostAsJsonAsync("/api/rentals", ValidRequest());

        response.StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task A_failure_is_returned_as_problem_details_carrying_the_error_code()
    {
        factory.RentalService
            .RequestAsync(Arg.Any<RequestRentalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<RentalDto>.Failure("rental.overlapping", "The vehicle is taken."));

        var response = await _client.PostAsJsonAsync("/api/rentals", ValidRequest());

        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("errorCode").GetString().ShouldBe("rental.overlapping");
        document.RootElement.GetProperty("detail").GetString().ShouldBe("The vehicle is taken.");
    }

    [Fact]
    public async Task Getting_an_existing_rental_returns_200()
    {
        var dto = SampleDto(status: "Confirmed");
        factory.RentalService.GetAsync(dto.Id, Arg.Any<CancellationToken>())
            .Returns(Result<RentalDto>.Success(dto));

        var response = await _client.GetAsync($"/api/rentals/{dto.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RentalDto>()).ShouldNotBeNull().Status.ShouldBe("Confirmed");
    }

    [Fact]
    public async Task Getting_an_unknown_rental_returns_404()
    {
        var id = Guid.CreateVersion7();
        factory.RentalService.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(Result<RentalDto>.Failure(RentalErrors.NotFound(id)));

        (await _client.GetAsync($"/api/rentals/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_non_guid_id_does_not_match_the_route_and_returns_404()
    {
        (await _client.GetAsync("/api/rentals/not-a-guid")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Listing_without_a_customer_id_returns_400()
    {
        (await _client.GetAsync("/api/rentals")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Listing_by_customer_returns_the_collection()
    {
        var customerId = Guid.CreateVersion7();
        IReadOnlyList<RentalDto> rentals = [SampleDto(), SampleDto()];
        factory.RentalService.ListByCustomerAsync(customerId, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RentalDto>>.Success(rentals));

        var response = await _client.GetAsync($"/api/rentals?customerId={customerId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<List<RentalDto>>()).ShouldNotBeNull().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Confirming_a_rental_returns_200_with_the_updated_state()
    {
        var dto = SampleDto(status: "Confirmed");
        factory.RentalService.ConfirmAsync(dto.Id, Arg.Any<CancellationToken>())
            .Returns(Result<RentalDto>.Success(dto));

        var response = await _client.PostAsync($"/api/rentals/{dto.Id}/confirm", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RentalDto>()).ShouldNotBeNull().Status.ShouldBe("Confirmed");
    }

    [Fact]
    public async Task Cancelling_a_rental_returns_the_refund_amount()
    {
        var dto = SampleDto(status: nameof(RentalStatus.Cancelled)) with { RefundAmount = 150m };
        factory.RentalService.CancelAsync(dto.Id, Arg.Any<CancellationToken>())
            .Returns(Result<RentalDto>.Success(dto));

        var response = await _client.PostAsync($"/api/rentals/{dto.Id}/cancel", null);

        (await response.Content.ReadFromJsonAsync<RentalDto>()).ShouldNotBeNull().RefundAmount.ShouldBe(150m);
    }

    [Fact]
    public async Task Malformed_json_is_rejected_with_400()
    {
        var content = new StringContent("{ not json", Encoding.UTF8, "application/json");

        (await _client.PostAsync("/api/rentals", content)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_rejected_with_415()
    {
        var content = new StringContent("plain text", Encoding.UTF8, "text/plain");

        (await _client.PostAsync("/api/rentals", content)).StatusCode
            .ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task The_liveness_probe_answers_without_touching_any_dependency()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("Healthy");
    }

    [Fact]
    public async Task An_incoming_correlation_id_is_echoed_back()
    {
        var correlationId = Guid.CreateVersion7().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Correlation-Id", correlationId);

        var response = await _client.SendAsync(request);

        response.Headers.GetValues("X-Correlation-Id").ShouldHaveSingleItem().ShouldBe(correlationId);
    }

    [Fact]
    public async Task A_correlation_id_is_generated_when_the_caller_does_not_send_one()
    {
        var response = await _client.GetAsync("/health");

        var value = response.Headers.GetValues("X-Correlation-Id").ShouldHaveSingleItem();
        Guid.TryParse(value, out _).ShouldBeTrue();
    }
}
