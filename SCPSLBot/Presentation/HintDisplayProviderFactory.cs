namespace SCPSLBot.Presentation
{
    internal static class HintDisplayProviderFactory
    {
        public static IHintDisplayProvider Create(HintDisplayConfig config)
        {
            return new HsmHintDisplayProvider(config);
        }
    }
}
