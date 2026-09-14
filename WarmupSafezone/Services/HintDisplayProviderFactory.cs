namespace ScpslPluginStarter.Services;

internal static class HintDisplayProviderFactory
{
    public static IHintDisplayProvider Create(HintDisplayConfig config)
    {
        HsmHintDisplayProvider hsm = new(config);
        if (hsm.TryInitialize(logResult: false))
        {
            return hsm;
        }

        return new NullHintDisplayProvider("HintServiceMeow is unavailable or incompatible.");
    }
}
