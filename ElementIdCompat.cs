using Autodesk.Revit.DB;

// Без namespace — виден из любого файла без дополнительного using (extension-методы
// в глобальном namespace резолвятся из любого вложенного namespace автоматически).
//
// Revit 2023 API (net48): ElementId.IntegerValue (int) — есть, ElementId.Value — нет.
// Revit 2026 API (net8.0): ElementId.IntegerValue — убран, есть только ElementId.Value (long).
// Из-за этого шаринга исходников между DAN_Plugin.csproj (2023) и DAN_Plugin2026.csproj
// (2026) прямое использование любого из двух свойств ломает один из двух таргетов.
internal static class ElementIdCompat
{
#if NET8_0_OR_GREATER
    public static long IntValue(this ElementId id) => id.Value;
    public static long IntValue(this WorksetId id) => id.IntegerValue;
    public static ElementId MakeElementId(long value) => new ElementId(value);
#else
    public static int IntValue(this ElementId id) => id.IntegerValue;
    public static int IntValue(this WorksetId id) => id.IntegerValue;
    public static ElementId MakeElementId(long value) => new ElementId((int)value);
#endif
}
