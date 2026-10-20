using MapGeneration;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Room.Beliefs
{
    /// <summary>Whether the bot can get from <see cref="From"/> into <see cref="To"/> right now (keycards carried, gates held open).</summary>
    internal class ZoneAccess : Belief<bool>
    {
        public ZoneAccess(FacilityZone from, FacilityZone to)
        {
            From = from;
            To = to;
        }

        public FacilityZone From { get; }
        public FacilityZone To { get; }

        public bool CanPass { get; private set; }

        public void Set(bool canPass)
        {
            if (canPass != CanPass)
            {
                CanPass = canPass;
                InvokeOnUpdate();
            }
        }

        public override string ToString()
        {
            return $"{nameof(ZoneAccess)}({From} -> {To}): {CanPass}";
        }
    }
}
