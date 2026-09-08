namespace KiCad.Automation.Model;

public static class Coordinates
{
    public static long MillimetersToNanometers(decimal value)
    {
        decimal scaled = checked(value * 1_000_000m);
        if (decimal.Truncate(scaled) != scaled)
            throw new AutomationException("invalid_precision", "Coordinates must be exactly representable in nanometers.");
        return checked((long)scaled);
    }

    public static long MillimetersToSchematicUnits(decimal value)
    {
        long nanometers = MillimetersToNanometers(value);
        if (nanometers % 100 != 0)
            throw new AutomationException("invalid_precision", "Schematic coordinates must be multiples of 100 nanometers.");
        return nanometers / 100;
    }

    public static decimal NanometersToMillimeters(long value) => value / 1_000_000m;
}
