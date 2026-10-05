namespace SCPSLBot.Api
{
    /// <summary>
    /// True until BotRoundControl enables server management. Warmup companions read this gate
    /// after management's load priority, and remain dormant when only the AI plugin is installed.
    /// </summary>
    public static class BotsOnlyMode
    {
        public static bool IsEnabled { get; internal set; }
    }
}
