using PlayerRoles;
using PlayerRoles.Subroutines;
using System;
using System.Collections.Generic;

namespace SCPSLBot.AI.FirstPersonControl.Combat.ScpAbilities
{
    /// <summary>
    /// Per-bot subroutine lookup that survives runtime role changes. The native module walks its
    /// subroutine array on every call; this resolves each type once per role instance.
    /// </summary>
    internal sealed class ScpAbilitySet
    {
        private readonly FpcBotPlayer botPlayer;
        private readonly Dictionary<Type, SubroutineBase> cache = new();
        private PlayerRoleBase cachedRole;

        public ScpAbilitySet(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        public ReferenceHub Hub => botPlayer.BotHub.PlayerHub;

        public PlayerRoleBase Role => Hub.roleManager.CurrentRole;

        /// <summary>Resolves a subroutine of the bot's current role; false when the role does not have it.</summary>
        public bool TryGet<T>(out T subroutine) where T : SubroutineBase
        {
            var role = Role;
            if (!ReferenceEquals(role, cachedRole))
            {
                cache.Clear();
                cachedRole = role;
            }

            if (!cache.TryGetValue(typeof(T), out var cached))
            {
                cached = null;
                if (role is ISubroutinedRole subroutined
                    && subroutined.SubroutineModule != null
                    && subroutined.SubroutineModule.TryGetSubroutine<T>(out var found))
                {
                    cached = found;
                }

                cache[typeof(T)] = cached;
            }

            // Pooled roles keep their subroutines alive, but a destroyed one must never be handed out.
            if (cached == null)
            {
                subroutine = null;
                return false;
            }

            subroutine = (T)cached;
            return true;
        }
    }
}
