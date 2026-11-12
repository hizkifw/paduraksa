using Jint;

namespace Paduraksa.Mcp;

/// <summary>The <c>search</c> tool's global: <c>spec</c>, the API description with its references resolved, built afresh in every call so a script's changes die with it.</summary>
internal sealed class SearchHost(SpecGraph spec) : SandboxHost
{
    public override void Install(Engine engine, SandboxLimits limits, SandboxBudget budget) => engine.SetValue("spec", spec.Build(engine));
}
