using System.Collections.Generic;
using Stellar.Abstractions.Domain.Inventory;

namespace Stellar.ModuleOptimizer.Tests;

internal static class TestModules
{
    // Interlocked: xunit runs test classes in parallel and this counter is
    // shared — a torn increment would mint duplicate uuids inside one pool
    // and break the distinctness assertions.
    private static long _uuid;

    internal static ModuleInfo Mod(
        ModuleCategory cat = ModuleCategory.Attack,
        params (int AttrId, int Value)[] parts)
    {
        var list = new List<ModulePart>();
        foreach (var (attrId, value) in parts)
        {
            list.Add(new ModulePart(attrId, $"attr{attrId}", value));
        }
        var id = System.Threading.Interlocked.Increment(ref _uuid);
        return new ModuleInfo(id, 1000, $"m{id}", 5, cat, list);
    }

    internal static ModuleSnapshot Snap(params ModuleInfo[] mods) => new(mods, 0);

    internal static long Choose(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        long result = 1;
        for (var i = 1; i <= k; i++) result = result * (n - k + i) / i;
        return result;
    }
}
