using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeSystemTray.Scaling
{
    /// <summary>
    /// A size written as pixels ("24px", or just 24) or as a percentage of a base size ("167%").
    /// Pixels are device-independent: at 150% Windows scaling, 24px covers 36 screen pixels.
    /// </summary>
    public readonly record struct SizeValue(double Value, bool IsPercent)
    {
        public static SizeValue Percent(double percent) => new(percent, true);

        public static SizeValue Pixels(double pixels) => new(pixels, false);

        /// <exception cref="FormatException">Not a number followed by "%" or "px" (or nothing, meaning px).</exception>
        public static SizeValue Parse(string? text)
        {
            string t = (text ?? "").Trim();
            bool percent = t.EndsWith('%');
            if (percent) t = t[..^1];
            else if (t.EndsWith("px", StringComparison.OrdinalIgnoreCase)) t = t[..^2];
            if (!double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                throw new FormatException($"'{text}' is not a size: write a percentage (\"150%\") or pixels (\"24px\")");
            return new SizeValue(value, percent);
        }

        /// <summary>The size in device-independent pixels, given what 100% is.</summary>
        public double Resolve(double baseSize) => IsPercent ? baseSize * Value / 100 : Value;

        public override string ToString() =>
            Value.ToString("0.###", CultureInfo.InvariantCulture) + (IsPercent ? "%" : "px");
    }

    /// <summary>Reads a JSON string or number (a number is pixels) into a string setting, so a typo does not break the whole file.</summary>
    public sealed class StringOrNumberConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException("Expected a string or a number")
        };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
}
