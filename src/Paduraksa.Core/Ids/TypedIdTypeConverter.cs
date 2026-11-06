using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Paduraksa.Core.Ids;

/// <summary>
/// <see cref="TypeConverter"/> between a typed ID and its string form, for consumers that bind through
/// <see cref="TypeDescriptor"/> (Blazor route/query parameters, configuration binding, property grids).
/// </summary>
/// <typeparam name="T">The typed ID.</typeparam>
public sealed class TypedIdTypeConverter<T> : TypeConverter
    where T : struct, ITypedId<T>
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override bool CanConvertTo(ITypeDescriptorContext? context, [NotNullWhen(true)] Type? destinationType) =>
        destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string s ? T.Parse(s.AsSpan()) : base.ConvertFrom(context, culture, value);

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) =>
        destinationType == typeof(string) && value is T id ? id.ToString() : base.ConvertTo(context, culture, value, destinationType);

    public override bool IsValid(ITypeDescriptorContext? context, object? value) =>
        value is T || (value is string s && T.TryParse(s.AsSpan(), out _));
}
