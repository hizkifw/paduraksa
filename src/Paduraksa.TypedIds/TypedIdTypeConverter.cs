using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Paduraksa.TypedIds;

/// <summary>
/// <see cref="TypeConverter"/> between a typed ID and its string form, for consumers that bind through
/// <see cref="TypeDescriptor"/> (route and query parameters, configuration binding, property grids). Applied by the generator to every
/// ID, so it is for generated code and not meant to be used directly.
/// </summary>
/// <typeparam name="T">The typed ID.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class TypedIdTypeConverter<T> : TypeConverter
    where T : struct, ITypedId<T>
{
    /// <inheritdoc />
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    /// <inheritdoc />
    public override bool CanConvertTo(ITypeDescriptorContext? context, [NotNullWhen(true)] Type? destinationType) =>
        destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

    /// <inheritdoc />
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string s ? T.Parse(s.AsSpan()) : base.ConvertFrom(context, culture, value);

    /// <inheritdoc />
    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) =>
        destinationType == typeof(string) && value is T id ? id.ToString() : base.ConvertTo(context, culture, value, destinationType);

    /// <inheritdoc />
    public override bool IsValid(ITypeDescriptorContext? context, object? value) =>
        value is T || (value is string s && T.TryParse(s.AsSpan(), out _));
}
