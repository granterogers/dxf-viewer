namespace DxfViewer;

// What one drawing unit physically means, per the file's $INSUNITS header. "Unknown" is
// the common case for this project's corpus: most real cutlist exports are headerless
// pre-R12 DXF and declare nothing, so the app must not pretend to know.
public enum CadUnits
{
    Unknown,
    Inches,
    Feet,
    Millimeters,
    Centimeters,
    Meters,
}

// What the user wants measurements displayed in.
public enum UnitSystem
{
    AsDrawn,
    Imperial,
    Metric,
}

internal static class UnitConvert
{
    // $INSUNITS values, per the DXF spec.
    public static CadUnits FromInsUnits(int code) => code switch
    {
        1 => CadUnits.Inches,
        2 => CadUnits.Feet,
        4 => CadUnits.Millimeters,
        5 => CadUnits.Centimeters,
        6 => CadUnits.Meters,
        _ => CadUnits.Unknown,
    };

    private static double ToMillimeters(CadUnits u) => u switch
    {
        CadUnits.Inches      => 25.4,
        CadUnits.Feet        => 304.8,
        CadUnits.Millimeters => 1.0,
        CadUnits.Centimeters => 10.0,
        CadUnits.Meters      => 1000.0,
        _                    => 0.0,   // unknown: no conversion factor exists
    };

    public static string Abbreviation(CadUnits u) => u switch
    {
        CadUnits.Inches      => "in",
        CadUnits.Feet        => "ft",
        CadUnits.Millimeters => "mm",
        CadUnits.Centimeters => "cm",
        CadUnits.Meters      => "m",
        _                    => "",
    };

    // Only 1 of the 313 sample DXFs declares $INSUNITS, so refusing to convert whenever
    // the header is silent would make the metric/imperial switch useless on essentially
    // every real file. These are Microvellum cabinetry cutlists, which are authored in
    // inches, so that is the assumed source when the file says nothing -- and the status
    // bar states the assumption rather than hiding it.
    public const CadUnits AssumedWhenUndeclared = CadUnits.Inches;

    public static CadUnits Effective(CadUnits declared) =>
        declared == CadUnits.Unknown ? AssumedWhenUndeclared : declared;

    // Formats a length measured in drawing units for display.
    public static string Format(double lengthInDrawingUnits, CadUnits source, UnitSystem target)
    {
        if (target == UnitSystem.AsDrawn)
        {
            string abbr = Abbreviation(source);
            return abbr.Length > 0
                ? $"{lengthInDrawingUnits:F4} {abbr}"
                : $"{lengthInDrawingUnits:F4} du";
        }

        double mm = lengthInDrawingUnits * ToMillimeters(Effective(source));

        if (target == UnitSystem.Metric)
            return mm >= 1000.0 ? $"{mm / 1000.0:F4} m" : $"{mm:F2} mm";

        double inches = mm / 25.4;
        if (inches >= 12.0)
        {
            int feet = (int)(inches / 12.0);
            double rem = inches - feet * 12.0;
            return $"{feet}' {rem:F3}\"";
        }
        return $"{inches:F4} in";
    }

    // Short label for the axis deltas, which stay in one consistent unit rather than
    // switching to feet-and-inches mid-readout.
    public static string FormatDelta(double lengthInDrawingUnits, CadUnits source, UnitSystem target)
    {
        if (target == UnitSystem.AsDrawn)
            return $"{lengthInDrawingUnits:F4}";

        double mm = lengthInDrawingUnits * ToMillimeters(Effective(source));
        return target == UnitSystem.Metric ? $"{mm:F2}" : $"{mm / 25.4:F4}";
    }

    public static string TargetAbbreviation(CadUnits source, UnitSystem target)
    {
        if (target == UnitSystem.AsDrawn)
            return Abbreviation(source) is { Length: > 0 } a ? a : "du";
        return target == UnitSystem.Metric ? "mm" : "in";
    }
}
