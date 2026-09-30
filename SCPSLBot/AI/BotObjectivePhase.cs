namespace SCPSLBot.AI
{
    public enum BotObjectivePhase
    {
        /// <summary>Walking toward the objective goal.</summary>
        Moving,

        /// <summary>Fighting a hostile in line of sight inside the engage radius.</summary>
        Engaging,

        /// <summary>Standing at the goal, or at the nearest reachable point when the goal cannot be reached.</summary>
        Holding,
    }
}
