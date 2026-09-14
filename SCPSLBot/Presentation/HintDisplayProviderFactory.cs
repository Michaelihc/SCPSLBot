namespace SCPSLBot.Presentation
{
    internal static class HintDisplayProviderFactory
    {
        public static IHintDisplayProvider Create(HintDisplayConfig config)
        {
            HsmHintDisplayProvider hsm = new(config);
            if (hsm.TryInitialize(logResult: false))
            {
                return hsm;
            }

            return new NullHintDisplayProvider(
                "HintServiceMeow.dll is missing or its required API is incompatible.");
        }
    }
}
