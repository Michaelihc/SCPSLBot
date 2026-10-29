using SCPSLBot.AI.FirstPersonControl.Brain;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Escape
{
    /// <summary>
    /// The goal state. Escaping changes the role (Class-D and scientists become Chaos or MTF), so
    /// the current life has escaped exactly when the bot no longer holds an objective role.
    /// </summary>
    internal class PlayerEscaped : Belief<bool>
    {
        private readonly FpcBotPlayer botPlayer;

        public PlayerEscaped(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        public bool Escaped
        {
            get
            {
                var role = botPlayer.BotHub.PlayerHub.roleManager.CurrentRole;
                return role == null || !ObjectiveBehavior.AppliesTo(role.RoleTypeId);
            }
        }

        public override string ToString()
        {
            return $"{nameof(PlayerEscaped)}: {Escaped}";
        }
    }
}
