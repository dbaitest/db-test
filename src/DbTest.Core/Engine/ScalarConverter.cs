using System.Globalization;

namespace DbTest.Core.Engine;

/// <summary>
/// Converts the raw value a driver returns from <c>ExecuteScalar</c> to the type the caller
/// asked for.
/// </summary>
/// <remarks>
/// <see cref="System.Convert.ChangeType(object, Type, IFormatProvider)"/> alone is not enough:
/// it rejects nullable targets (<c>int?</c>), enums, <see cref="Guid"/> from text and
/// <see cref="DateTimeOffset"/> from <see cref="DateTime"/>. The drivers disagree on exactly
/// these types — MySQL hands back a <c>CHAR(36)</c> where SQL Server hands back a
/// <c>uniqueidentifier</c> — so the shared test body needs one conversion that covers them.
/// </remarks>
internal static class ScalarConverter
{
    /// <summary>Converts <paramref name="value"/> to <typeparamref name="T"/>; null and DBNull give default.</summary>
    /// <exception cref="InvalidCastException">The value cannot be converted.</exception>
    public static T? Convert<T>(object? value)
    {
        if (value is null or DBNull)
        {
            return default;
        }

        if (value is T typed)
        {
            return typed;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)ConvertTo(value, target);
    }

    private static object ConvertTo(object value, Type target)
    {
        if (target.IsInstanceOfType(value))
        {
            return value;
        }

        if (target.IsEnum)
        {
            return value is string name
                ? Enum.Parse(target, name, ignoreCase: true)
                : Enum.ToObject(target, System.Convert.ChangeType(value, Enum.GetUnderlyingType(target), CultureInfo.InvariantCulture));
        }

        if (target == typeof(Guid))
        {
            return value switch
            {
                string text => Guid.Parse(text),
                // Only MySQL hands a Guid back as bytes (BINARY(16)), and UUID_TO_BIN stores
                // the RFC 4122 big-endian layout, not .NET's mixed-endian one.
                byte[] bytes when bytes.Length == 16 => new Guid(bytes, bigEndian: true),
                _ => throw CannotConvert(value, target),
            };
        }

        if (target == typeof(DateTimeOffset))
        {
            return value switch
            {
                DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, dateTime.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dateTime.Kind)),
                string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
                _ => throw CannotConvert(value, target),
            };
        }

        if (target == typeof(TimeSpan) && value is string span)
        {
            return TimeSpan.Parse(span, CultureInfo.InvariantCulture);
        }

        if (value is IConvertible)
        {
            return System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }

        throw CannotConvert(value, target);
    }

    private static InvalidCastException CannotConvert(object value, Type target) =>
        new($"Cannot convert a {value.GetType().Name} scalar to {target.Name}.");
}
