using System;
using System.Collections.Generic;

namespace ScpslPluginStarter.Core;

internal sealed class ProjectileBlockList
{
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public ProjectileBlockList(IEnumerable<string>? names)
    {
        foreach (string name in names ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _names.Add(name.Trim());
            }
        }
    }

    public IEnumerable<string> Names => _names;

    public bool Contains(string itemTypeName) => _names.Contains(itemTypeName);
}
