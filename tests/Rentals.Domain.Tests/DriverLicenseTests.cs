using Rentals.Domain.Exceptions;
using Rentals.Domain.Model;
using TestSupport;

namespace Rentals.Domain.Tests;

public sealed class DriverLicenseTests
{
    private static readonly DateTimeOffset Base = FixedClock.DefaultNow;

    [Fact]
    public void Create_normalizes_the_number_to_uppercase_and_trims_it()
    {
        DriverLicense.Create("  lic-12345 ", Base.AddYears(1)).Number.ShouldBe("LIC-12345");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_an_empty_number(string number)
    {
        Should.Throw<InvalidDriverLicenseException>(() => DriverLicense.Create(number, Base.AddYears(1)));
    }

    [Theory]
    [InlineData("ABC")]                        // demasiado corto
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWX")]   // demasiado largo
    [InlineData("LIC 12345")]                  // espacios no permitidos
    [InlineData("LIC_12345")]                  // guion bajo no permitido
    public void Create_rejects_numbers_outside_the_expected_format(string number)
    {
        var exception = Should.Throw<InvalidDriverLicenseException>(
            () => DriverLicense.Create(number, Base.AddYears(1)));

        exception.Code.ShouldBe("rental.invalid_license");
    }

    [Fact]
    public void CoversPeriod_is_true_when_the_license_outlives_the_rental()
    {
        var license = DriverLicense.Create("LIC-12345", Base.AddYears(1));
        var period = RentalPeriod.Create(Base, Base.AddDays(5));

        license.CoversPeriod(period).ShouldBeTrue();
    }

    [Fact]
    public void CoversPeriod_is_true_when_the_license_expires_exactly_at_the_end()
    {
        var period = RentalPeriod.Create(Base, Base.AddDays(5));
        var license = DriverLicense.Create("LIC-12345", period.End);

        license.CoversPeriod(period).ShouldBeTrue();
    }

    [Fact]
    public void CoversPeriod_is_false_when_the_license_expires_before_the_return()
    {
        var period = RentalPeriod.Create(Base, Base.AddDays(5));
        var license = DriverLicense.Create("LIC-12345", period.End.AddSeconds(-1));

        license.CoversPeriod(period).ShouldBeFalse();
    }
}
