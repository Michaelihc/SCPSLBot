using CommandSystem;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.AI.Commands
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    internal sealed class BotOrderCommand : ICommand
    {
        public string Command => "bot_order";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Order one or all SCPSLBot bots: moveto x y z, hold, release, or objective x y z radius.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.PlayersManagement, out response))
            {
                return false;
            }

            if (!BotCommandParsing.TryParseOrder(arguments, out var request, out response))
            {
                return false;
            }

            var bots = BotManager.Instance.BotPlayers.Keys
                .Where(hub => hub != null && (request.AllBots || hub.PlayerId == request.PlayerId))
                .ToArray();
            if (bots.Length == 0)
            {
                response = request.AllBots
                    ? "There are no SCPSLBot bots."
                    : $"Player {request.PlayerId} is not an SCPSLBot dummy.";
                return false;
            }

            var point = new Vector3(request.X, request.Y, request.Z);
            var failed = new List<int>();
            foreach (var hub in bots)
            {
                if (!Apply(hub, request, point))
                {
                    failed.Add(hub.PlayerId);
                }
            }

            var verb = request.Verb.ToString().ToLowerInvariant();
            var applied = bots.Length - failed.Count;
            response = failed.Count == 0
                ? $"bot_order {verb} applied to {applied}/{bots.Length} bots."
                : $"bot_order {verb} applied to {applied}/{bots.Length} bots; failed player_ids={string.Join(",", failed)} (see [BotOrders] log lines).";
            return applied > 0;
        }

        private static bool Apply(ReferenceHub hub, BotOrderRequest request, Vector3 point)
        {
            return request.Verb switch
            {
                BotOrderVerb.MoveTo => BotOrders.MoveTo(hub, point),
                BotOrderVerb.Hold => BotOrders.Stop(hub),
                BotOrderVerb.Release => BotOrders.Release(hub),
                BotOrderVerb.Objective => BotOrders.SetObjective(hub, point, request.EngageRadius),
                _ => false,
            };
        }
    }
}
