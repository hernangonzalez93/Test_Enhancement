using System.Text.RegularExpressions;
using Rentals.Domain.Common;
using Rentals.Domain.Exceptions;

namespace Rentals.Domain.Model;

/// <summary>
/// Licencia de conduccion del titular. No se puede rentar si la licencia
/// vence antes de que el vehiculo deba ser devuelto.
/// </summary>
public sealed partial class DriverLicense : ValueObject
{
    [GeneratedRegex("^[A-Z0-9-]{5,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex LicensePattern();

    private DriverLicense(string number, DateTimeOffset expiresOn)
    {
        Number = number;
        ExpiresOn = expiresOn;
    }

    public string Number { get; }

    public DateTimeOffset ExpiresOn { get; }

    public static DriverLicense Create(string number, DateTimeOffset expiresOn)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            throw new InvalidDriverLicenseException("the number is required.");
        }

        var normalized = number.Trim().ToUpperInvariant();
        if (!LicensePattern().IsMatch(normalized))
        {
            throw new InvalidDriverLicenseException("the number does not match the expected format.");
        }

        return new DriverLicense(normalized, expiresOn.ToUniversalTime());
    }

    public bool CoversPeriod(RentalPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        return ExpiresOn >= period.End;
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Number;
        yield return ExpiresOn;
    }

    public override string ToString() => Number;
}
