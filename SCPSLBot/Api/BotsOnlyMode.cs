namespace SCPSLBot.Api
{
    /// <summary>
    /// The configured bots_only master switch. Warmup companions read it when they enable, which
    /// load priority places after SCPSLBot, and stay dormant while it is set.
    /// </summary>
    public static class BotsOnlyMode
    {
        public static bool IsEnabled { get; internal set; }
    }
}
