#nullable enable

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SCPSLBot.Infrastructure;

/// <summary>
/// Lifetime collections must remain usable after Unity destroys a component's native object.
/// ReferenceHub.GetHashCode dereferences its GameObject; numeric player IDs can be recycled.
/// </summary>
internal sealed class ManagedReferenceComparer<T> : IEqualityComparer<T> where T : class
{
    public static ManagedReferenceComparer<T> Instance { get; } = new();

    private ManagedReferenceComparer() { }

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) => ReferenceEquals(obj, null) ? 0 : RuntimeHelpers.GetHashCode(obj);
}
