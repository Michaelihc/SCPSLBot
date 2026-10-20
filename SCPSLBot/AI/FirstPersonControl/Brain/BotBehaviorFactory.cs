namespace SCPSLBot.AI.FirstPersonControl.Brain
{
    /// <summary>
    /// The one place a bot's non-combat behaviors are registered. Every behavior is added for every
    /// bot; a behavior that does not apply to the current role scores 0 in Evaluate.
    /// <para>
    /// To add behaviors, append a line in <see cref="Build"/> that registers them, e.g.
    /// <c>LootBehaviors.AddTo(brain, bot);</c>, with a static <c>AddTo(BotBrain, FpcBotPlayer)</c>
    /// next to the behavior. Behaviors move only through <see cref="FpcBotPlayer.MoveToPosition(UnityEngine.Vector3)"/>
    /// (locomotion crosses doors and elevators), stand still with <c>bot.Locomotion.Stop()</c>, take
    /// the hands through <see cref="FpcBotPlayer.ItemLease"/>, and give up a goal when
    /// <c>bot.Locomotion.ConsumeAbandonRequest()</c> returns true.
    /// </para>
    /// </summary>
    internal static class BotBehaviorFactory
    {
        public static BotBrain Build(FpcBotPlayer bot)
        {
            var brain = new BotBrain();
            brain.Add(new ObjectiveBehavior(bot));
            brain.Add(new ExploreBehavior(bot));

            // Behaviors owned by other features register below.

            return brain;
        }
    }
}
