using Billing.Domain;

namespace Billing.Domain.Tests;

/// <summary>
/// Nivel 1 del servicio de facturacion: reglas puras, sin infraestructura.
/// Mismo criterio que en Rentals — se cubre cada transicion legal y, sobre todo,
/// cada transicion ilegal.
/// </summary>
public sealed class InvoiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid RentalId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid CustomerId = Guid.Parse("66666666-7777-8888-9999-000000000000");

    private static Invoice Draft() => Invoice.DraftFor(RentalId, CustomerId, "USD", Now);

    private static Invoice Issued(decimal amount = 100m)
    {
        var invoice = Draft();
        invoice.AddLine("rental", Money.Of(amount, "USD"));
        invoice.Issue(Now);
        return invoice;
    }

    [Fact]
    public void A_new_invoice_starts_as_a_draft_without_lines()
    {
        var invoice = Draft();

        invoice.Status.ShouldBe(InvoiceStatus.Draft);
        invoice.Lines.ShouldBeEmpty();
        invoice.Total.IsZero.ShouldBeTrue();
        invoice.IssuedAt.ShouldBeNull();
    }

    [Fact]
    public void The_number_is_derived_from_the_rental_so_it_is_stable()
    {
        Draft().Number.ShouldBe(Invoice.NumberFor(RentalId));
        Invoice.NumberFor(RentalId).ShouldBe(Invoice.NumberFor(RentalId));
    }

    [Fact]
    public void The_tax_and_the_total_are_computed_from_the_lines()
    {
        var invoice = Draft();
        invoice.AddLine("rental", Money.Of(100m, "USD"));
        invoice.AddLine("extras", Money.Of(50m, "USD"));

        invoice.Subtotal.Amount.ShouldBe(150m);
        invoice.Tax.Amount.ShouldBe(28.50m);     // 19 % de 150
        invoice.Total.Amount.ShouldBe(178.50m);
    }

    [Theory]
    [InlineData(100, 19, 119)]
    [InlineData(0.01, 0, 0.01)]      // el impuesto se redondea a la baja
    [InlineData(33.33, 6.33, 39.66)]
    public void The_tax_is_rounded_to_two_decimals(decimal subtotal, decimal expectedTax, decimal expectedTotal)
    {
        var invoice = Draft();
        invoice.AddLine("rental", Money.Of(subtotal, "USD"));

        invoice.Tax.Amount.ShouldBe(expectedTax);
        invoice.Total.Amount.ShouldBe(expectedTotal);
    }

    [Fact]
    public void A_line_in_another_currency_is_rejected()
    {
        var invoice = Draft();

        Should.Throw<CurrencyMismatchException>(() => invoice.AddLine("rental", Money.Of(10m, "EUR")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_line_without_concept_is_rejected(string concept)
    {
        var invoice = Draft();

        Should.Throw<InvalidInvoiceLineException>(() => invoice.AddLine(concept, Money.Of(10m, "USD")));
    }

    [Fact]
    public void A_line_with_a_zero_amount_is_rejected()
    {
        var invoice = Draft();

        Should.Throw<InvalidInvoiceLineException>(() => invoice.AddLine("rental", Money.Zero("USD")));
    }

    [Fact]
    public void An_empty_invoice_cannot_be_issued()
    {
        var exception = Should.Throw<EmptyInvoiceException>(() => Draft().Issue(Now));

        exception.Code.ShouldBe("invoice.empty");
    }

    [Fact]
    public void Issuing_moves_the_invoice_to_issued_and_stamps_the_date()
    {
        var invoice = Issued();

        invoice.Status.ShouldBe(InvoiceStatus.Issued);
        invoice.IssuedAt.ShouldBe(Now);
    }

    [Fact]
    public void Issuing_twice_is_rejected()
    {
        var invoice = Issued();

        Should.Throw<InvalidInvoiceStateException>(() => invoice.Issue(Now));
    }

    [Fact]
    public void Lines_cannot_be_added_once_the_invoice_is_issued()
    {
        var invoice = Issued();

        var exception = Should.Throw<InvalidInvoiceStateException>(
            () => invoice.AddLine("late", Money.Of(10m, "USD")));

        exception.CurrentState.ShouldBe(nameof(InvoiceStatus.Issued));
    }

    [Fact]
    public void Paying_the_exact_total_marks_the_invoice_as_paid()
    {
        var invoice = Issued(100m);

        invoice.Pay(Money.Of(119m, "USD"), Now);

        invoice.Status.ShouldBe(InvoiceStatus.Paid);
        invoice.PaidAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData(100)]   // se paga el subtotal, olvidando el impuesto
    [InlineData(120)]
    public void Paying_a_different_amount_is_rejected(decimal paid)
    {
        var invoice = Issued(100m);

        var exception = Should.Throw<PaymentMismatchException>(() => invoice.Pay(Money.Of(paid, "USD"), Now));

        exception.Code.ShouldBe("invoice.payment_mismatch");
        invoice.Status.ShouldBe(InvoiceStatus.Issued);
    }

    [Fact]
    public void A_draft_invoice_cannot_be_paid()
    {
        var invoice = Draft();
        invoice.AddLine("rental", Money.Of(100m, "USD"));

        Should.Throw<InvalidInvoiceStateException>(() => invoice.Pay(Money.Of(119m, "USD"), Now));
    }

    [Fact]
    public void A_draft_invoice_can_be_voided()
    {
        var invoice = Draft();

        invoice.Void(Now);

        invoice.Status.ShouldBe(InvoiceStatus.Void);
    }

    [Fact]
    public void An_issued_invoice_can_be_voided()
    {
        var invoice = Issued();

        invoice.Void(Now);

        invoice.Status.ShouldBe(InvoiceStatus.Void);
    }

    [Fact]
    public void A_paid_invoice_cannot_be_voided()
    {
        var invoice = Issued(100m);
        invoice.Pay(Money.Of(119m, "USD"), Now);

        var exception = Should.Throw<InvalidInvoiceStateException>(() => invoice.Void(Now));

        exception.CurrentState.ShouldBe(nameof(InvoiceStatus.Paid));
    }

    [Fact]
    public void Voiding_twice_is_rejected()
    {
        var invoice = Draft();
        invoice.Void(Now);

        Should.Throw<InvalidInvoiceStateException>(() => invoice.Void(Now));
    }

    [Fact]
    public void An_invoice_without_a_rental_is_rejected()
    {
        Should.Throw<ArgumentException>(() => Invoice.DraftFor(Guid.Empty, CustomerId, "USD", Now));
    }

    [Fact]
    public void An_invalid_currency_is_rejected_at_creation()
    {
        Should.Throw<InvalidCurrencyException>(() => Invoice.DraftFor(RentalId, CustomerId, "US", Now));
    }
}

/// <summary>El Money propio del contexto de facturacion.</summary>
public sealed class MoneyTests
{
    [Fact]
    public void Amounts_are_rounded_to_two_decimals_away_from_zero()
    {
        Money.Of(10.005m, "usd").Amount.ShouldBe(10.01m);
    }

    [Fact]
    public void The_currency_is_normalized_to_uppercase()
    {
        Money.Of(10m, " eur ").Currency.ShouldBe("EUR");
    }

    [Fact]
    public void Negative_amounts_are_rejected()
    {
        Should.Throw<NegativeAmountException>(() => Money.Of(-0.01m, "USD"));
    }

    [Fact]
    public void Adding_a_different_currency_is_rejected()
    {
        Should.Throw<CurrencyMismatchException>(() => Money.Of(10m, "USD").Add(Money.Of(1m, "EUR")));
    }

    [Fact]
    public void Two_amounts_with_the_same_value_are_equal()
    {
        Money.Of(10m, "USD").Equals(Money.Of(10m, "USD")).ShouldBeTrue();
        (Money.Of(10m, "USD") == Money.Of(10m, "USD")).ShouldBeTrue();
    }

    [Fact]
    public void Percentage_applies_the_given_share()
    {
        Money.Of(200m, "USD").Percentage(19m).Amount.ShouldBe(38m);
    }
}
