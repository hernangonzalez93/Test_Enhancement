using Billing.Application;
using Billing.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Billing.Application.Tests;

/// <summary>
/// Nivel 2 del servicio de facturacion: orquestacion con dobles, igual que en
/// Rentals. No se reprueban las reglas del agregado; se prueba la coordinacion
/// —idempotencia, persistencia, traduccion de errores— y las decisiones que solo
/// existen en esta capa.
/// </summary>
public sealed class InvoiceServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid RentalId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid CustomerId = Guid.Parse("66666666-7777-8888-9999-000000000000");

    private readonly IInvoiceRepository _repository = Substitute.For<IInvoiceRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public InvoiceServiceTests() => _clock.UtcNow.Returns(Now);

    private InvoiceService CreateSut() =>
        new(_repository, _unitOfWork, _clock, NullLogger<InvoiceService>.Instance);

    private static Invoice ExistingInvoice()
    {
        var invoice = Invoice.DraftFor(RentalId, CustomerId, "USD", Now);
        invoice.AddLine("rental", Money.Of(100m, "USD"));
        invoice.Issue(Now);
        return invoice;
    }

    [Fact]
    public async Task A_completed_rental_produces_an_issued_invoice_with_tax()
    {
        var result = await CreateSut().IssueForCompletedRentalAsync(RentalId, CustomerId, 200m, 0, "USD");

        result.IsSuccess.ShouldBeTrue();
        var invoice = result.Value.ShouldNotBeNull();
        invoice.Status.ShouldBe(nameof(InvoiceStatus.Issued));
        invoice.Subtotal.ShouldBe(200m);
        invoice.Tax.ShouldBe(38m);
        invoice.Total.ShouldBe(238m);
        invoice.Lines.ShouldHaveSingleItem().Concept.ShouldBe("rental");
    }

    [Fact]
    public async Task Issuing_persists_and_commits()
    {
        await CreateSut().IssueForCompletedRentalAsync(RentalId, CustomerId, 200m, 0, "USD");

        Received.InOrder(() =>
        {
            _repository.AddAsync(Arg.Any<Invoice>(), Arg.Any<CancellationToken>());
            _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Reprocessing_a_completion_returns_the_existing_invoice_without_creating_another()
    {
        // Kafka entrega al menos una vez: el mismo evento puede llegar dos veces.
        _repository.GetByRentalAsync(RentalId, Arg.Any<CancellationToken>()).Returns(ExistingInvoice());

        var result = await CreateSut().IssueForCompletedRentalAsync(RentalId, CustomerId, 200m, 0, "USD");

        result.IsSuccess.ShouldBeTrue();
        await _repository.DidNotReceive().AddAsync(Arg.Any<Invoice>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_cancellation_bills_only_the_non_refunded_penalty()
    {
        // Total 300, reembolso 150 -> penalizacion 150, mas 19 % de impuesto.
        var result = await CreateSut().IssueForCancelledRentalAsync(RentalId, CustomerId, 300m, 150m, "USD");

        var invoice = result.Value.ShouldNotBeNull();
        invoice.Subtotal.ShouldBe(150m);
        invoice.Total.ShouldBe(178.50m);
        invoice.Lines.ShouldHaveSingleItem().Concept.ShouldBe("cancellation-penalty");
    }

    [Fact]
    public async Task A_fully_refunded_cancellation_produces_no_invoice()
    {
        var result = await CreateSut().IssueForCancelledRentalAsync(RentalId, CustomerId, 300m, 300m, "USD");

        result.Error.Code.ShouldBe("invoice.nothing_to_bill");
        await _repository.DidNotReceive().AddAsync(Arg.Any<Invoice>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Paying_settles_the_invoice_for_its_exact_total()
    {
        var invoice = ExistingInvoice();
        _repository.GetByIdAsync(invoice.Id, Arg.Any<CancellationToken>()).Returns(invoice);

        var result = await CreateSut().PayAsync(invoice.Id.Value);

        result.Value.ShouldNotBeNull().Status.ShouldBe(nameof(InvoiceStatus.Paid));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Paying_an_unknown_invoice_returns_not_found()
    {
        var result = await CreateSut().PayAsync(Guid.CreateVersion7());

        result.Error.Code.ShouldBe("invoice.not_found");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Voiding_a_paid_invoice_is_translated_into_a_failed_result()
    {
        var invoice = ExistingInvoice();
        invoice.Pay(invoice.Total, Now);
        _repository.GetByIdAsync(invoice.Id, Arg.Any<CancellationToken>()).Returns(invoice);

        var result = await CreateSut().VoidAsync(invoice.Id.Value);

        result.Error.Code.ShouldBe("invoice.invalid_state");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_invalid_currency_is_translated_instead_of_throwing()
    {
        var result = await CreateSut().IssueForCompletedRentalAsync(RentalId, CustomerId, 200m, 0, "US");

        result.Error.Code.ShouldBe("money.invalid_currency");
    }

    [Fact]
    public async Task Listing_by_customer_maps_every_invoice()
    {
        _repository.ListByCustomerAsync(CustomerId, Arg.Any<CancellationToken>())
            .Returns([ExistingInvoice(), ExistingInvoice()]);

        var result = await CreateSut().ListByCustomerAsync(CustomerId);

        result.Value.ShouldNotBeNull().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Getting_by_rental_returns_not_found_when_there_is_no_invoice()
    {
        var result = await CreateSut().GetByRentalAsync(RentalId);

        result.Error.Code.ShouldBe("invoice.not_found");
    }
}
