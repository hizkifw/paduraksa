using Microsoft.OpenApi;

namespace Paduraksa.Admin.OpenApi;

/// <summary>Applies <see cref="DescriptionText.Tidy"/> to every description and summary in the generated document, wherever the generator or a transformer put it.</summary>
internal sealed class DescriptionWalker
{
    private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);

    public static void Tidy(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        new DescriptionWalker().Visit(document);
    }

    private static IEnumerable<TValue> Values<TKey, TValue>(IDictionary<TKey, TValue>? dictionary) => dictionary?.Values ?? Enumerable.Empty<TValue>();

    private static string? Text(string? text) => DescriptionText.Tidy(text);

    private void Visit(OpenApiDocument document)
    {
        if (document.Info is { } info)
        {
            info.Description = Text(info.Description);
            info.Summary = Text(info.Summary);
        }

        foreach (var server in document.Servers ?? [])
        {
            server.Description = Text(server.Description);
        }

        foreach (var tag in document.Tags ?? new HashSet<OpenApiTag>())
        {
            tag.Description = Text(tag.Description);
        }

        if (document.Components is { } components)
        {
            foreach (var schema in Values(components.Schemas))
            {
                Visit(schema);
            }

            foreach (var parameter in Values(components.Parameters))
            {
                Visit(parameter);
            }

            foreach (var response in Values(components.Responses))
            {
                Visit(response);
            }

            foreach (var body in Values(components.RequestBodies))
            {
                Visit(body);
            }

            foreach (var scheme in Values(components.SecuritySchemes))
            {
                if (scheme is OpenApiSecurityScheme concrete)
                {
                    concrete.Description = Text(concrete.Description);
                }
            }
        }

        foreach (var item in Values(document.Paths))
        {
            if (item is OpenApiPathItem path)
            {
                path.Summary = Text(path.Summary);
                path.Description = Text(path.Description);
            }

            foreach (var parameter in item.Parameters ?? [])
            {
                Visit(parameter);
            }

            foreach (var operation in Values(item.Operations))
            {
                Visit(operation);
            }
        }
    }

    private void Visit(OpenApiOperation operation)
    {
        operation.Summary = Text(operation.Summary);
        operation.Description = Text(operation.Description);
        foreach (var parameter in operation.Parameters ?? [])
        {
            Visit(parameter);
        }

        Visit(operation.RequestBody);
        foreach (var response in Values(operation.Responses))
        {
            Visit(response);
        }
    }

    private void Visit(IOpenApiParameter? parameter)
    {
        if (parameter is null || !_seen.Add(parameter))
        {
            return;
        }

        switch (parameter)
        {
            case OpenApiParameter concrete:
                concrete.Description = Text(concrete.Description);
                break;
            case OpenApiParameterReference reference:
                reference.Description = Text(reference.Description);
                break;
        }

        Visit(parameter.Schema);
        Visit(parameter.Content);
    }

    private void Visit(IOpenApiRequestBody? body)
    {
        if (body is null || !_seen.Add(body))
        {
            return;
        }

        switch (body)
        {
            case OpenApiRequestBody concrete:
                concrete.Description = Text(concrete.Description);
                break;
            case OpenApiRequestBodyReference reference:
                reference.Description = Text(reference.Description);
                break;
        }

        Visit(body.Content);
    }

    private void Visit(IOpenApiResponse? response)
    {
        if (response is null || !_seen.Add(response))
        {
            return;
        }

        switch (response)
        {
            case OpenApiResponse concrete:
                concrete.Description = Text(concrete.Description);
                break;
            case OpenApiResponseReference reference:
                reference.Description = Text(reference.Description);
                break;
        }

        foreach (var header in Values(response.Headers))
        {
            if (header is OpenApiHeader concrete)
            {
                concrete.Description = Text(concrete.Description);
            }

            Visit(header.Schema);
        }

        Visit(response.Content);
    }

    private void Visit(IDictionary<string, OpenApiMediaType>? content)
    {
        foreach (var media in Values(content))
        {
            Visit(media.Schema);
        }
    }

    private void Visit(IOpenApiSchema? schema)
    {
        if (schema is null || !_seen.Add(schema))
        {
            return;
        }

        switch (schema)
        {
            case OpenApiSchema concrete:
                concrete.Description = Text(concrete.Description);
                break;
            case OpenApiSchemaReference reference:
                reference.Description = Text(reference.Description);
                break;
        }

        foreach (var property in Values(schema.Properties))
        {
            Visit(property);
        }

        Visit(schema.Items);
        Visit(schema.AdditionalProperties);
        foreach (var part in (schema.AllOf ?? []).Concat(schema.AnyOf ?? []).Concat(schema.OneOf ?? []))
        {
            Visit(part);
        }
    }
}
