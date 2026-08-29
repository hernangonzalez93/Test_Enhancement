using Rentals.Domain.Model;

namespace TestSupport;

/// <summary>
/// Object Mother / Builder del agregado. Cada prueba declara solo lo que le
/// importa (la fecha, el estado, la tarifa) y hereda un caso valido para todo
/// lo demas. Es lo que mantiene legibles ~100 pruebas.
/// </summary>
public sealed class RentalBuilder
{
    private RentalId _id = RentalId.New();
    private CustomerId _customerId = CustomerId.New();
    private VehicleId _vehicleId = VehicleId.New();
    private DateTimeOffset _now = FixedClock.DefaultNow;
    private DateTimeOffset _start = FixedClock.DefaultNow.AddDays(10);
    private DateTimeOffset _end = FixedClock.DefaultNow.AddDays(13);
    private string _licenseNumber = "LIC-12345";
    private DateTimeOffset _licenseExpiry = FixedClock.DefaultNow.AddYears(3);
    private decimal _dailyRate = 50m;
    private string _currency = "USD";

    public static RentalBuilder A() => new();

    public RentalBuilder WithId(RentalId id)
    {
        _id = id;
        return this;
    }

    public RentalBuilder ForCustomer(CustomerId customerId)
    {
        _customerId = customerId;
        return this;
    }

    public RentalBuilder ForVehicle(VehicleId vehicleId)
    {
        _vehicleId = vehicleId;
        return this;
    }

    public RentalBuilder Now(DateTimeOffset now)
    {
        _now = now;
        return this;
    }

    public RentalBuilder From(DateTimeOffset start)
    {
        _start = start;
        return this;
    }

    public RentalBuilder To(DateTimeOffset end)
    {
        _end = end;
        return this;
    }

    public RentalBuilder ForDays(int days)
    {
        _end = _start.AddDays(days);
        return this;
    }

    public RentalBuilder WithDailyRate(decimal amount, string currency = "USD")
    {
        _dailyRate = amount;
        _currency = currency;
        return this;
    }

    public RentalBuilder WithLicense(string number, DateTimeOffset? expiresOn = null)
    {
        _licenseNumber = number;
        if (expiresOn is not null)
        {
            _licenseExpiry = expiresOn.Value;
        }

        return this;
    }

    public RentalBuilder WithLicenseExpiringOn(DateTimeOffset expiresOn)
    {
        _licenseExpiry = expiresOn;
        return this;
    }

    public Rental Build() => Rental.Request(
        _id,
        _customerId,
        _vehicleId,
        RentalPeriod.Create(_start, _end),
        DriverLicense.Create(_licenseNumber, _licenseExpiry),
        Money.Of(_dailyRate, _currency),
        _now);

    /// <summary>Renta ya confirmada, sin eventos pendientes de la creacion.</summary>
    public Rental BuildConfirmed()
    {
        var rental = Build();
        rental.Confirm(_now);
        return rental;
    }

    /// <summary>Renta en curso: confirmada y con el vehiculo ya retirado.</summary>
    public Rental BuildActive()
    {
        var rental = BuildConfirmed();
        rental.Start(_start);
        return rental;
    }

    public Rental BuildCompleted(DateTimeOffset? returnedAt = null)
    {
        var rental = BuildActive();
        rental.Complete(returnedAt ?? _end);
        return rental;
    }

    public Rental BuildCancelled(DateTimeOffset? cancelledAt = null)
    {
        var rental = BuildConfirmed();
        rental.Cancel(cancelledAt ?? _now);
        return rental;
    }
}
