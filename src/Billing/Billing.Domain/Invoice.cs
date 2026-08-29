namespace Billing.Domain;

public sealed class InvalidInvoiceStateException(string currentState, string attemptedTransition)
    : BillingDomainException($"Cannot {attemptedTransition} an invoice in state '{currentState}'.")
{
    public override string Code => "invoice.invalid_state";

    public string CurrentState { get; } = currentState;
}

public sealed class EmptyInvoiceException()
    : BillingDomainException("An invoice needs at least one line before it can be issued.")
{
    public override string Code => "invoice.empty";
}

public sealed class PaymentMismatchException(decimal expected, decimal received)
    : BillingDomainException($"The invoice total is {expected} but {received} was paid.")
{
    public override string Code => "invoice.payment_mismatch";
}

public sealed class InvalidInvoiceLineException(string reason)
    : BillingDomainException($"Invalid invoice line: {reason}")
{
    public override string Code => "invoice.invalid_line";
}

public readonly record struct InvoiceId(Guid Value)
{
    public static InvoiceId New() => new(Guid.CreateVersion7());

    public static InvoiceId From(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("InvoiceId cannot be empty.", nameof(value))
        : new InvoiceId(value);

    public override string ToString() => Value.ToString();
}

public enum InvoiceStatus
{
    Draft = 0,
    Issued = 1,
    Paid = 2,
    Void = 3
}

/// <summary>Concepto facturado. Inmutable una vez anadido.</summary>
public sealed class InvoiceLine : ValueObject
{
    // EF Core no puede enlazar un constructor cuyo parametro sea un owned type
    // (Money es una navegacion, no una propiedad escalar). Con este constructor
    // sin parametros materializa por propiedades, que si admiten setter privado.
    private InvoiceLine()
    {
    }

    private InvoiceLine(string concept, Money amount)
    {
        Concept = concept;
        Amount = amount;
    }

    public string Concept { get; private set; } = string.Empty;

    public Money Amount { get; private set; } = null!;

    public static InvoiceLine Create(string concept, Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (string.IsNullOrWhiteSpace(concept))
        {
            throw new InvalidInvoiceLineException("the concept is required.");
        }

        if (amount.IsZero)
        {
            throw new InvalidInvoiceLineException("the amount must be greater than zero.");
        }

        return new InvoiceLine(concept.Trim(), amount);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Concept;
        yield return Amount;
    }
}

/// <summary>
/// Raiz del agregado de facturacion. Concentra las reglas: que se puede anadir,
/// cuando se puede emitir, cuanto hay que pagar y que transiciones son legales.
/// No conoce EF Core, HTTP ni Kafka.
///
/// Draft -> Issued -> Paid, con salida a Void desde Draft e Issued.
/// Una factura pagada es inmutable: no se anula, se rectifica (fuera de alcance).
/// </summary>
public sealed class Invoice
{
    /// <summary>IVA aplicado. Constante del dominio, no configuracion.</summary>
    public const decimal TaxRatePercentage = 19m;

    private readonly List<InvoiceLine> _lines = [];

    private Invoice()
    {
        Number = string.Empty;
        Currency = string.Empty;
    }

    private Invoice(InvoiceId id, string number, Guid rentalId, Guid customerId, string currency, DateTimeOffset createdAt)
    {
        Id = id;
        Number = number;
        RentalId = rentalId;
        CustomerId = customerId;
        Currency = currency;
        CreatedAt = createdAt;
        Status = InvoiceStatus.Draft;
    }

    public InvoiceId Id { get; private set; }

    public string Number { get; private set; }

    public Guid RentalId { get; private set; }

    public Guid CustomerId { get; private set; }

    public string Currency { get; private set; }

    public InvoiceStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? IssuedAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public DateTimeOffset? VoidedAt { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines.AsReadOnly();

    public Money Subtotal => _lines.Count == 0
        ? Money.Zero(Currency)
        : _lines.Select(line => line.Amount).Aggregate(static (left, right) => left.Add(right));

    public Money Tax => Subtotal.Percentage(TaxRatePercentage);

    public Money Total => Subtotal.Add(Tax);

    /// <summary>
    /// El numero se deriva de la renta, de modo que reprocesar el evento que
    /// origina la factura no produce dos numeraciones distintas.
    /// </summary>
    public static string NumberFor(Guid rentalId) =>
        "INV-" + rentalId.ToString("N")[..8].ToUpperInvariant();

    public static Invoice DraftFor(Guid rentalId, Guid customerId, string currency, DateTimeOffset now)
    {
        if (rentalId == Guid.Empty)
        {
            throw new ArgumentException("rentalId is required.", nameof(rentalId));
        }

        // Valida la moneda por la via del value object antes de guardarla plana.
        var normalized = Money.Zero(currency).Currency;

        return new Invoice(InvoiceId.New(), NumberFor(rentalId), rentalId, customerId, normalized, now.ToUniversalTime());
    }

    public void AddLine(string concept, Money amount)
    {
        EnsureStatusIs(InvoiceStatus.Draft, "add a line to");
        ArgumentNullException.ThrowIfNull(amount);

        if (!string.Equals(amount.Currency, Currency, StringComparison.Ordinal))
        {
            throw new CurrencyMismatchException(Currency, amount.Currency);
        }

        _lines.Add(InvoiceLine.Create(concept, amount));
    }

    public void Issue(DateTimeOffset now)
    {
        EnsureStatusIs(InvoiceStatus.Draft, "issue");

        if (_lines.Count == 0)
        {
            throw new EmptyInvoiceException();
        }

        Status = InvoiceStatus.Issued;
        IssuedAt = now.ToUniversalTime();
    }

    /// <summary>El pago debe cuadrar exactamente con el total: no hay pagos parciales.</summary>
    public void Pay(Money amount, DateTimeOffset now)
    {
        EnsureStatusIs(InvoiceStatus.Issued, "pay");
        ArgumentNullException.ThrowIfNull(amount);

        if (amount != Total)
        {
            throw new PaymentMismatchException(Total.Amount, amount.Amount);
        }

        Status = InvoiceStatus.Paid;
        PaidAt = now.ToUniversalTime();
    }

    public void Void(DateTimeOffset now)
    {
        if (Status is not (InvoiceStatus.Draft or InvoiceStatus.Issued))
        {
            throw new InvalidInvoiceStateException(Status.ToString(), "void");
        }

        Status = InvoiceStatus.Void;
        VoidedAt = now.ToUniversalTime();
    }

    private void EnsureStatusIs(InvoiceStatus expected, string transition)
    {
        if (Status != expected)
        {
            throw new InvalidInvoiceStateException(Status.ToString(), transition);
        }
    }
}
