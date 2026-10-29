namespace SCPSLBot.AI.FirstPersonControl.Mind.Escape
{
    /// <summary>
    /// Whether every zone border between the bot and Surface is passable now. When it is, the
    /// escape is one trip: the navmesh plans the whole multi-zone route and locomotion crosses
    /// the checkpoints, gates and elevators on it.
    /// </summary>
    internal class EscapeRouteAccess : Belief<bool>
    {
        public bool CanReachExit { get; private set; }

        public void Set(bool canReachExit)
        {
            if (canReachExit != CanReachExit)
            {
                CanReachExit = canReachExit;
                InvokeOnUpdate();
            }
        }

        public override string ToString()
        {
            return $"{nameof(EscapeRouteAccess)}: {CanReachExit}";
        }
    }
}
