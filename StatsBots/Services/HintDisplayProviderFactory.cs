using System;
using System.Linq;
using StatsBots.Config;

namespace StatsBots.Services;

internal static class HintDisplayProviderFactory
{
    public static IHintDisplayProvider Create(HintDisplayConfig config)
    {
        bool hsmLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .Any(static assembly => string.Equals(assembly.GetName().Name, "HintServiceMeow", StringComparison.OrdinalIgnoreCase));
        if (hsmLoaded)
        {
            var provider = new HsmHintDisplayProvider(config);
            if (provider.TryInitialize(false)) return provider;
        }

        return new NullHintDisplayProvider("HintServiceMeow is unavailable; StatsBots HUD is disabled without touching the shared native hint channel.");
    }
}
