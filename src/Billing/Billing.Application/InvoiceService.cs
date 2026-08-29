using Billing.Domain;
using Microsoft.Extensions.Logging;

namespace Billing.Application;

// ---------- Puertos de salida ----------

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IInvoiceRepository
{
    Task<Invoice?> GetByIdAsync(InvoiceId id, CancellationToken cancellationToken = default);

    Task<Invoice?> GetByRentalAsync(Guid rentalId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Invoice>> ListByCustomerAsync(Guid customerId, CancellationToken cancellationToken = default);

    Task AddAsync(Invoice invoice, CancellationToken cancellationToken = default);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

// ---------- Resultado y errores ----------

public sealed record Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);
}

public sealed class Result<T>
{
    private Result(bool isSuccess, T? value, Error error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public T? Value { get; }

    public Error Error { get; }

    public static Result<T> Success(T value) => new(true, value, Error.None);

    public static Result<T> Failure(Error error) => new(false, default, error);

    public static Result<T> Failure(string code, string message) => Failure(new Error(code, message));
}

public static class InvoiceErrors
{
    public static Error NotFound(Guid id) => new("invoice.not_found", "Invoice " + id + " was not found.");

    public static readonly Error NothingToBill =
        new("invoice.nothing_to_bill", "The cancellation was fully refunded, so there is nothing to bill.");
}

// ---------- DTOs ----------

public sealed record InvoiceLineDto(string Concept, decimal Amount);

public sealed record InvoiceDto(
    Guid Id,
    string Number,
    Guid RentalId,
    Guid CustomerId,
    string Status,
    string Currency,
    decimal Subtotal,
    decimal Tax,
    decimal Total,
    IReadOnlyList<InvoiceLineDto> Lines,
    DateTimeOffset CreatedAt,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? PaidAt)
{
    public static InvoiceDto From(Invoice invoice) => new(
        invoice.Id.Value,
        invoice.Number,
        invoice.RentalId,
        invoice.CustomerId,
        invoice.Status.ToString(),
        invoice.Currency,
        invoice.Subtotal.Amount,
        invoice.Tax.Amount,
        invoice.Total.Amount,
        invoice.Lines.Select(line => new InvoiceLineDto(line.Concept, line.Amount.Amount)).ToList(),
        invoice.CreatedAt,
        invoice.IssuedAt,
        invoice.PaidAt);
}

// ---------- Puerto de entrada ----------

public interface IInvoiceService
{
    Task<Result<InvoiceDto>> IssueForCompletedRentalAsync(
        Guid rentalId, Guid customerId, decimal finalTotal, int lateDays, string currency,
        CancellationToken cancellationToken = default);

    Task<Result<InvoiceDto>> IssueForCancelledRentalAsync(
        Guid rentalId, Guid customerId, decimal estimatedTotal, decimal refundAmount, string currency,
        CancellationToken cancellationToken = default);

    Task<Result<InvoiceDto>> PayAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    Task<Result<InvoiceDto>> VoidAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    Task<Result<InvoiceDto>> GetAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    Task<Result<InvoiceDto>> GetByRentalAsync(Guid rentalId, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<InvoiceDto>>> ListByCustomerAsync(Guid customerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Orquestacion del caso de uso. Sin reglas de negocio: todas viven en el
/// agregado <see cref="Invoice"/>. Aqui solo se coordinan repositorio, unidad de
/// trabajo y reloj, y se traduce el resultado.
/// </summary>
public sealed class InvoiceService(
    IInvoiceRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<InvoiceService> logger) : IInvoiceService
{
    public async Task<Result<InvoiceDto>> IssueForCompletedRentalAsync(
        Guid rentalId,
        Guid customerId,
        decimal finalTotal,
        int lateDays,
        string currency,
        CancellationToken cancellationToken = default)
    {
        // Idempotencia: Kafka entrega al menos una vez, asi que reprocesar el
        // evento debe devolver la factura existente, no crear una segunda.
        var existing = await repository.GetByRentalAsync(rentalId, cancellationToken);
        if (existing is not null)
        {
            return Result<InvoiceDto>.Success(InvoiceDto.From(existing));
        }

        try
        {
            var invoice = Invoice.DraftFor(rentalId, customerId, currency, clock.UtcNow);
            invoice.AddLine("rental", Money.Of(finalTotal, currency));

            if (lateDays > 0)
            {
                logger.LogInformation("Rental {RentalId} was returned {LateDays} day(s) late.", rentalId, lateDays);
            }

            invoice.Issue(clock.UtcNow);

            return await PersistAsync(invoice, cancellationToken);
        }
        catch (BillingDomainException exception)
        {
            return Result<InvoiceDto>.Failure(exception.Code, exception.Message);
        }
    }

    public async Task<Result<InvoiceDto>> IssueForCancelledRentalAsync(
        Guid rentalId,
        Guid customerId,
        decimal estimatedTotal,
        decimal refundAmount,
        string currency,
        CancellationToken cancellationToken = default)
    {
        var existing = await repository.GetByRentalAsync(rentalId, cancellationToken);
        if (existing is not null)
        {
            return Result<InvoiceDto>.Success(InvoiceDto.From(existing));
        }

        var penalty = estimatedTotal - refundAmount;
        if (penalty <= 0m)
        {
            // Reembolso total: no hay nada que cobrar y por tanto no hay factura.
            return Result<InvoiceDto>.Failure(InvoiceErrors.NothingToBill);
        }

        try
        {
            var invoice = Invoice.DraftFor(rentalId, customerId, currency, clock.UtcNow);
            invoice.AddLine("cancellation-penalty", Money.Of(penalty, currency));
            invoice.Issue(clock.UtcNow);

            return await PersistAsync(invoice, cancellationToken);
        }
        catch (BillingDomainException exception)
        {
            return Result<InvoiceDto>.Failure(exception.Code, exception.Message);
        }
    }

    public Task<Result<InvoiceDto>> PayAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        MutateAsync(invoiceId, (invoice, now) => invoice.Pay(invoice.Total, now), cancellationToken);

    public Task<Result<InvoiceDto>> VoidAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        MutateAsync(invoiceId, (invoice, now) => invoice.Void(now), cancellationToken);

    public async Task<Result<InvoiceDto>> GetAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await repository.GetByIdAsync(InvoiceId.From(invoiceId), cancellationToken);

        return invoice is null
            ? Result<InvoiceDto>.Failure(InvoiceErrors.NotFound(invoiceId))
            : Result<InvoiceDto>.Success(InvoiceDto.From(invoice));
    }

    public async Task<Result<InvoiceDto>> GetByRentalAsync(Guid rentalId, CancellationToken cancellationToken = default)
    {
        var invoice = await repository.GetByRentalAsync(rentalId, cancellationToken);

        return invoice is null
            ? Result<InvoiceDto>.Failure(InvoiceErrors.NotFound(rentalId))
            : Result<InvoiceDto>.Success(InvoiceDto.From(invoice));
    }

    public async Task<Result<IReadOnlyList<InvoiceDto>>> ListByCustomerAsync(
        Guid customerId,
        CancellationToken cancellationToken = default)
    {
        var invoices = await repository.ListByCustomerAsync(customerId, cancellationToken);
        IReadOnlyList<InvoiceDto> dtos = invoices.Select(InvoiceDto.From).ToList();

        return Result<IReadOnlyList<InvoiceDto>>.Success(dtos);
    }

    private async Task<Result<InvoiceDto>> PersistAsync(Invoice invoice, CancellationToken cancellationToken)
    {
        await repository.AddAsync(invoice, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Issued invoice {Number} for rental {RentalId}.", invoice.Number, invoice.RentalId);

        return Result<InvoiceDto>.Success(InvoiceDto.From(invoice));
    }

    private async Task<Result<InvoiceDto>> MutateAsync(
        Guid invoiceId,
        Action<Invoice, DateTimeOffset> transition,
        CancellationToken cancellationToken)
    {
        var invoice = await repository.GetByIdAsync(InvoiceId.From(invoiceId), cancellationToken);
        if (invoice is null)
        {
            return Result<InvoiceDto>.Failure(InvoiceErrors.NotFound(invoiceId));
        }

        try
        {
            transition(invoice, clock.UtcNow);
        }
        catch (BillingDomainException exception)
        {
            return Result<InvoiceDto>.Failure(exception.Code, exception.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<InvoiceDto>.Success(InvoiceDto.From(invoice));
    }
}
