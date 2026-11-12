using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using Microsoft.SqlServer.Dac;

namespace DbAiTest.MsSql.Dacpac;

/// <summary>
/// A stable fingerprint of <see cref="DacDeployOptions"/>, so two sources that would deploy a
/// different schema never share a container.
/// </summary>
/// <remarks>
/// <para>
/// Built by reflection over every public readable property rather than a hand-kept list: a
/// DacFx release that adds an option is then covered without a change here. Collections are
/// sorted, because the order an option list was filled in does not change the deploy.
/// </para>
/// <para>
/// Every value is length-prefixed, so no value can imitate a separator (<c>{A:"1,B:2"}</c>
/// and <c>{A:"1",B:"2"}</c> differ), and the result is hashed: the options can hold SQLCMD
/// values and storage keys, and the fingerprint ends up in <see cref="DbAiTest.Core.Schema.SchemaSource.Key"/>.
/// </para>
/// </remarks>
internal static class DeployOptionsFingerprint
{
    /// <summary>The fingerprint of "no options given": the source's defaults apply.</summary>
    public const string Defaults = "defaults";

    /// <summary>How deep nested DacFx option objects are described. DacFx 170 nests one level.</summary>
    private const int MaxDepth = 3;

    public static string Of(DacDeployOptions? options)
    {
        if (options is null)
        {
            return Defaults;
        }

        var text = Describe(options, depth: 0);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string Describe(object value, int depth)
    {
        var parts = value.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => Part(p.Name) + Part(Format(Read(p, value), depth)));

        return string.Concat(parts);
    }

    private static object? Read(PropertyInfo property, object owner)
    {
        try
        {
            return property.GetValue(owner);
        }
        catch (TargetInvocationException ex)
        {
            // A getter that throws still contributes a stable value rather than failing the source.
            return "<" + ex.InnerException?.GetType().Name + ">";
        }
    }

    private static string Format(object? value, int depth)
    {
        switch (value)
        {
            case null:
                return "null";
            case string text:
                return text;
            case IDictionary dictionary:
                return "{" + string.Concat(
                    dictionary.Keys.Cast<object>()
                        .Select(key => Part(Format(key, depth)) + Part(Format(dictionary[key], depth)))
                        .Order(StringComparer.Ordinal)) + "}";
            case IEnumerable sequence:
                return "[" + string.Concat(
                    sequence.Cast<object?>()
                        .Select(item => Part(Format(item, depth)))
                        .Order(StringComparer.Ordinal)) + "]";
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                // A nested DacFx options object is described by its own public state, to a bounded
                // depth so a type with a back-reference cannot recurse forever.
                var type = value.GetType();
                if (type.IsClass
                    && type.Namespace?.StartsWith("Microsoft.SqlServer.Dac", StringComparison.Ordinal) == true)
                {
                    return depth >= MaxDepth ? type.Name : type.Name + "(" + Describe(value, depth + 1) + ")";
                }

                return value.ToString() ?? string.Empty;
        }
    }

    /// <summary>Length-prefixes a value so the concatenation stays unambiguous.</summary>
    private static string Part(string value) => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
}
