namespace SCPSLBot.AI.FirstPersonControl.Brain.Scp
{
    /// <summary>
    /// Registers the out-of-combat SCP ability behaviors on a bot's brain. Each behavior scores 0
    /// unless the bot currently has its role, so one registration covers runtime role changes.
    /// </summary>
    internal static class ScpBehaviors
    {
        public static BotBrain AddTo(BotBrain brain, FpcBotPlayer bot)
        {
            return brain
                .Add(new Scp049ReviveBehavior(bot))
                .Add(new Scp0492ConsumeBehavior(bot))
                .Add(new Scp3114DisguiseBehavior(bot))
                .Add(new Scp939AmbushBehavior(bot));
        }
    }
}
