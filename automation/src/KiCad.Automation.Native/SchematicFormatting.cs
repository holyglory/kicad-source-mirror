using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

internal static class SchematicFormatting
{
    internal static void Validate(SchematicFormattingSettings value)
    {
        static bool Distance(long nm, int minimumMils, int maximumMils) =>
            nm >= minimumMils * 25400L && nm <= maximumMils * 25400L && nm % 100 == 0;
        static bool Range(string value, char unit) => value == unit.ToString()
            || value.Length == 2 && value[1] == unit && "~fpnumKMGTP".Contains(value[0]);
        var operating = value.OperatingPoint;
        var notation = value.UnitReference;
        if (!Distance(value.DefaultLineWidthNm, 5, 1000)
            || !Distance(value.DefaultTextSizeNm, 5, 1000)
            || !Distance(value.PinSymbolSizeNm, 0, 1000)
            || !Distance(value.ConnectionGridNm, 25, 10000)
            || value.JunctionSizeChoice > 5 || value.HopOverSizeChoice > 5
            || value.ReferencePrefix.Contains('\0') || value.ReferenceSuffix.Contains('\0')
            || operating is null || operating.VoltagePrecision is < 1 or > 10
            || operating.CurrentPrecision is < 1 or > 10
            || !Range(operating.VoltageRange, 'V') || !Range(operating.CurrentRange, 'A')
            || notation is null || notation.SeparatorAscii > 126 || notation.FirstIdAscii is < 49 or > 122)
            throw new AutomationException("unsupported_schematic_delta",
                "Project formatting requires native distances/choices, NUL-free delimiters, operating-point precision 1–10 with native ranges, and explicit unit-reference character codes in the native persisted ranges.");
    }
}
