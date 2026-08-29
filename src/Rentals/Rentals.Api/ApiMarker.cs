namespace Rentals.Api;

/// <summary>
/// Ancla de ensamblado para WebApplicationFactory. Se usa en lugar de la clase
/// Program generada por las top-level statements porque esa vive en el espacio
/// de nombres global: un proyecto de pruebas que referencie varias APIs a la
/// vez tendria varias clases Program ambiguas.
/// </summary>
public sealed class RentalsApiMarker;
