using Paduraksa.Proxy.Http;

namespace Paduraksa.Proxy.Config;

/// <summary>
/// Collects one host's response-header operations within the bound the gate's stack buffer allows
/// (<see cref="ResponseHeaders.MaxResponseHeaderOperations"/>). A list longer than that is a compile error, so no
/// snapshot can reach the gate with more operations than it has room for.
/// </summary>
internal sealed class ResponseHeaderOperationBuilder(CompileErrorSource where, List<CompileError> errors)
{
    private readonly List<ResponseHeaderOperation> _operations = new(ResponseHeaders.MaxResponseHeaderOperations);
    private bool _refused;

    public void Add(ResponseHeaderOperation operation)
    {
        if (_operations.Count < ResponseHeaders.MaxResponseHeaderOperations)
        {
            _operations.Add(operation);
        }
        else if (!_refused)
        {
            _refused = true;
            errors.Add(new(where, $"a host has at most {ResponseHeaders.MaxResponseHeaderOperations} response-header operations"));
        }
    }

    public ResponseHeaderOperation[] Build() => [.. _operations];
}
