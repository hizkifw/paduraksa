using Jint;

namespace Paduraksa.Mcp;

/// <summary>What a tool puts in the engine's global scope and how it serves the host calls its functions queue.</summary>
internal abstract class SandboxHost
{
    /// <summary>Sets the globals. The engine already has its guards and the budget is not armed yet, so setup is not charged to the script.</summary>
    public abstract void Install(Engine engine, SandboxLimits limits, SandboxBudget budget);

    /// <summary>
    /// Serves the oldest queued host call (blocking the sandbox thread, with the interpreter clock stopped) and settles its promise.
    /// </summary>
    /// <returns><c>false</c> when nothing is queued.</returns>
    public virtual bool ServeNext() => false;

    /// <summary>Called when the function has returned, before what it left queued is completed.</summary>
    public virtual void FunctionReturned()
    {
    }

    public virtual int Requests => 0;

    /// <summary>A note for the result once the function has returned, e.g. requests it left unawaited that were completed.</summary>
    public virtual string? Note => null;
}
