using System.Globalization;

namespace UndertaleModTool.Android.Ui;

/// <summary>
/// Parses user input into primitive / enum values.
/// </summary>
public static class ValueParser
{
    public static bool IsEditable(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal);
    }

    public static bool TryParse(string text, Type type, out object value)
    {
        value = null;
        Type underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            if (string.IsNullOrEmpty(text))
                return true;
            type = underlying;
        }

        text = text?.Trim() ?? "";
        CultureInfo c = CultureInfo.InvariantCulture;
        NumberStyles n = NumberStyles.Integer;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && type != typeof(float) && type != typeof(double))
        {
            n = NumberStyles.HexNumber;
            text = text[2..];
        }

        bool ok;
        switch (Type.GetTypeCode(type))
        {
            case TypeCode.String: value = text; return true;
            case TypeCode.Boolean: ok = bool.TryParse(text, out bool b); value = b; return ok;
            case TypeCode.Char: ok = text.Length == 1; value = ok ? text[0] : '\0'; return ok;
            case TypeCode.SByte: ok = sbyte.TryParse(text, n, c, out sbyte sb); value = sb; return ok;
            case TypeCode.Byte: ok = byte.TryParse(text, n, c, out byte by); value = by; return ok;
            case TypeCode.Int16: ok = short.TryParse(text, n, c, out short s); value = s; return ok;
            case TypeCode.UInt16: ok = ushort.TryParse(text, n, c, out ushort us); value = us; return ok;
            case TypeCode.Int32 when !type.IsEnum: ok = int.TryParse(text, n, c, out int i); value = i; return ok;
            case TypeCode.UInt32 when !type.IsEnum: ok = uint.TryParse(text, n, c, out uint ui); value = ui; return ok;
            case TypeCode.Int64 when !type.IsEnum: ok = long.TryParse(text, n, c, out long l); value = l; return ok;
            case TypeCode.UInt64 when !type.IsEnum: ok = ulong.TryParse(text, n, c, out ulong ul); value = ul; return ok;
            case TypeCode.Single: ok = float.TryParse(text, NumberStyles.Float, c, out float f); value = f; return ok;
            case TypeCode.Double: ok = double.TryParse(text, NumberStyles.Float, c, out double d); value = d; return ok;
            case TypeCode.Decimal: ok = decimal.TryParse(text, NumberStyles.Float, c, out decimal m); value = m; return ok;
        }

        if (type.IsEnum)
        {
            ok = Enum.TryParse(type, text, true, out object e);
            value = e;
            return ok;
        }
        return false;
    }

    public static string Format(object value) => value switch
    {
        null => "",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
