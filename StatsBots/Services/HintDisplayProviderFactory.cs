using System;
using System.Linq;
using StatsBots.Config;

namespace StatsBots.Services;

internal static class HintDisplayProviderFactory
{
    public static IHintDisplayProvider Create(HintDisplayConfig config)
    {
        return new HsmHintDisplayProvider(config);
    }
}
