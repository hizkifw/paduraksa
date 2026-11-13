using System.Reflection;
using System.Reflection.Emit;

using Paduraksa.Dashboard.Config;

namespace Paduraksa.Tests.Architecture;

/// <summary>
/// Every config change the dashboard makes goes through one path (docs/design/dashboard.md "Making a change"): a form, a toggle or a menu builds a
/// <c>ReviewRequest</c>, and the review sheet is the only place that applies it or adds it to the draft. The draft's own review applies the draft.
/// A component that called the config's execute or add-to-draft methods itself would skip the choices, the lock-out guard and the toast with Undo, so
/// this reads the IL of every method of the dashboard assembly (compiler-generated state machines and lambdas included) for such calls.
/// </summary>
public sealed class DashboardChangePathTests
{
    private const string Dashboard = "Paduraksa.Dashboard";

    private static readonly string ReviewSheet = $"{Dashboard}.Components.ReviewPanel";
    private static readonly string DraftDrawer = $"{Dashboard}.Components.DraftReviewDrawer";
    private static readonly string Undo = $"{Dashboard}.Config.ConfigUndo";
    private static readonly string DraftStateType = $"{Dashboard}.Config.DraftState";

    /// <summary>What may commit or draft a change, and who may call it. A call from anywhere else fails the test.</summary>
    private static readonly (string Declaring, string Method, string[] Allowed)[] Guarded =
    [
        // Applies a command: the review sheet, and the undo toast (a rollback of the revision the toast is about, which asks first while a draft is open).
        (nameof(IDashboardConfig), nameof(IDashboardConfig.ExecuteAsync), [ReviewSheet, Undo]),
        ("DashboardConfig", nameof(IDashboardConfig.ExecuteAsync), [ReviewSheet, Undo]),

        // Adds to the draft: only the draft state's own method, which only the review sheet calls.
        (nameof(IDashboardConfig), nameof(IDashboardConfig.AddToDraftAsync), [DraftStateType]),
        ("DashboardConfig", nameof(IDashboardConfig.AddToDraftAsync), [DraftStateType]),
        (nameof(DraftState), nameof(DraftState.AddAsync), [ReviewSheet]),

        // Applies the draft: the draft drawer, with the token of the review on screen.
        (nameof(IDashboardConfig), nameof(IDashboardConfig.ApplyDraftAsync), [DraftDrawer]),
        ("DashboardConfig", nameof(IDashboardConfig.ApplyDraftAsync), [DraftDrawer]),
    ];

    [Fact]
    public void OnlyTheReviewSheetAppliesOrDraftsAChange()
    {
        var violations = Calls()
            .Where(call => Guarded.Any(g => g.Declaring == call.Target.DeclaringType!.Name && g.Method == call.Target.Name
                && !g.Allowed.Contains(OutermostName(call.Caller.DeclaringType!))))
            .Select(call => $"{call.Caller.DeclaringType!.FullName}.{call.Caller.Name} calls {call.Target.DeclaringType!.Name}.{call.Target.Name}")
            .Distinct()
            .Order()
            .ToList();

        Assert.True(violations.Count == 0, "These bypass the review sheet (build a ReviewRequest and open it with Drawers.ReviewAsync, or ReviewDrawer):\n" + string.Join('\n', violations));
    }

    [Fact]
    public void TheScannerSeesTheCallsItGuards()
    {
        // A scan that finds nothing would pass the test above whatever the components did.
        var seen = Calls().Select(call => (Caller: OutermostName(call.Caller.DeclaringType!), call.Target.Name)).ToHashSet();

        Assert.Contains((ReviewSheet, nameof(IDashboardConfig.ExecuteAsync)), seen);
        Assert.Contains((Undo, nameof(IDashboardConfig.ExecuteAsync)), seen);
        Assert.Contains((ReviewSheet, nameof(DraftState.AddAsync)), seen);
        Assert.Contains((DraftStateType, nameof(IDashboardConfig.AddToDraftAsync)), seen);
        Assert.Contains((DraftDrawer, nameof(IDashboardConfig.ApplyDraftAsync)), seen);
    }

    private static string OutermostName(Type type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type.FullName!;
    }

    private static readonly Dictionary<short, OpCode> OpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(op => op.Value);

    private static IEnumerable<(MethodBase Caller, MethodBase Target)> Calls()
    {
        var assembly = typeof(DraftState).Assembly;
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
            {
                if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
                {
                    continue;
                }

                foreach (var token in CallTokens(il))
                {
                    MethodBase? target;
                    try
                    {
                        target = method.Module.ResolveMethod(token, type.IsGenericType ? type.GetGenericArguments() : null, method.IsGenericMethod ? method.GetGenericArguments() : null);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    if (target?.DeclaringType is not null)
                    {
                        yield return (method, target);
                    }
                }
            }
        }
    }

    private static IEnumerable<int> CallTokens(byte[] il)
    {
        for (var i = 0; i < il.Length;)
        {
            var code = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
            i += il[i] == 0xFE ? 2 : 1;
            var op = OpCodes[code];
            if (op.OperandType == OperandType.InlineMethod && op.Value is 0x28 or 0x6F)
            {
                yield return BitConverter.ToInt32(il, i);
            }

            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                _ => 4,
            };
        }
    }
}
