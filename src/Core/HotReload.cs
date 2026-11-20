using System.Reflection.Metadata;

[assembly: MetadataUpdateHandler(typeof(Cosmiria.HotReload))]

namespace Cosmiria;

/// <summary>
/// .NET Hot Reload integration (<c>dotnet watch</c>). When code changes are applied, including a
/// regenerated Render from an edited .cml file, every live <see cref="UIContext"/> rebuilds on its
/// next frame. Component state survives because it lives in the framework's tables.
/// </summary>
public static class HotReload
{
    private static readonly List<WeakReference<UIContext>> contexts = new();
    private static readonly object gate = new();

    /// <summary>Raised (on the hot reload agent's thread) after an update was applied.</summary>
    public static event Action<Type[]> Updated;

    private static volatile int version;

    /// <summary>Incremented on every applied update. Generated CML code uses it to rebuild cached styles.</summary>
    public static int Version => version;

    /// <summary>Number of hot reload updates applied since startup.</summary>
    public static int UpdateCount => version;

    internal static void Register(UIContext context)
    {
        lock (gate)
        {
            contexts.RemoveAll(w => !w.TryGetTarget(out _));
            contexts.Add(new WeakReference<UIContext>(context));
        }
    }

    // Called by the runtime; signatures are fixed by MetadataUpdateHandlerAttribute.
    internal static void ClearCache(Type[] updatedTypes) { }

    internal static void UpdateApplication(Type[] updatedTypes)
    {
        lock (gate)
        {
            version++;
            foreach (var weak in contexts)
            {
                if (weak.TryGetTarget(out var ctx)) ctx.Notify();
            }
        }
        Updated?.Invoke(updatedTypes ?? Array.Empty<Type>());
    }
}
