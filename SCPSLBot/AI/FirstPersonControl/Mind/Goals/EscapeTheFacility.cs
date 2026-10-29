using SCPSLBot.AI.FirstPersonControl.Mind.Escape;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Goals
{
    internal class EscapeTheFacility : IGoal
    {
        public void SetEnabledByBeliefs(FpcMind fpcMind)
        {
            fpcMind.GoalEnabledBy<PlayerEscaped, bool>(this, b => true, b => b.Escaped);
        }
    }
}
