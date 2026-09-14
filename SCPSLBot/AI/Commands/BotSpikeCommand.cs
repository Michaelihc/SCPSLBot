using CommandSystem;
using LabLogger = LabApi.Features.Console.Logger;
using MapGeneration;
using MEC;
using PlayerRoles;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.Commands
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    internal sealed class BotSpikeCommand : ICommand
    {
        public string Command => "botspike";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Watchable native-FPC bot movement spike: start, walk, tour, status, stop, cleanup.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (arguments.Count == 0)
            {
                response = Usage;
                return false;
            }

            var action = arguments.At(0).ToLowerInvariant();
            if (action == "status")
            {
                if (!sender.CheckPermission(PlayerPermissions.GameplayData, out response))
                {
                    return false;
                }

                response = BotSpikeDemo.Status();
                return true;
            }

            if (!sender.CheckPermission(PlayerPermissions.PlayersManagement, out response))
            {
                return false;
            }

            switch (action)
            {
                case "start":
                    return BotSpikeDemo.Start(out response);
                case "walk":
                    return BotSpikeDemo.Walk(arguments.Count > 1 ? arguments.At(1) : "preset", out response);
                case "tour":
                    return BotSpikeDemo.Tour(out response);
                case "stop":
                    return BotSpikeDemo.Stop(out response);
                case "cleanup":
                    return BotSpikeDemo.Cleanup(out response);
                default:
                    response = Usage;
                    return false;
            }
        }

        private const string Usage = "Usage: botspike start | walk <RoomName|preset|offmesh> | tour | status | stop | cleanup";
    }

    internal static class BotSpikeDemo
    {
        private static readonly RoomName[] PresetRoute =
        {
            RoomName.LczToilets,
            RoomName.Lcz173,
        };

        private static readonly RoomName[] TourRoute =
        {
            RoomName.LczToilets,
            RoomName.Lcz173,
            RoomName.Lcz914,
            RoomName.LczClassDSpawn,
        };

        private static ReferenceHub bot;
        private static CoroutineHandle routeHandle;
        private static bool routeRunning;
        private static string routeLabel = "none";

        public static bool Start(out string response)
        {
            StopRoute();
            if (bot != null)
            {
                BotOrders.DespawnBot(bot);
                bot = null;
            }

            bot = BotOrders.SpawnBot("BotOrders Spike", RoleTypeId.ClassD);
            if (bot == null)
            {
                response = "Failed to spawn the spike bot. Check the server log.";
                return false;
            }

            // Hold as soon as its FPC role arrives; the role system performs the only spawn placement.
            BotOrders.Stop(bot);
            LabLogger.Info($"[BotOrders] SPIKE_START bot={BotName} requestedRole={RoleTypeId.ClassD} placement=native-role-spawn");
            response = "Spawned BotOrders Spike as ClassD via native role spawn. Wait about 2 seconds, then use botspike walk preset.";
            return true;
        }

        public static bool Walk(string target, out string response)
        {
            if (!EnsureBot(out response))
            {
                return false;
            }

            if (string.Equals(target, "preset", StringComparison.OrdinalIgnoreCase))
            {
                BeginRoute(PresetRoute, "preset");
                response = $"Started preset route: {string.Join(" -> ", PresetRoute)}.";
                return true;
            }

            if (string.Equals(target, "offmesh", StringComparison.OrdinalIgnoreCase))
            {
                StopRoute();
                var offMeshGoal = bot.transform.position + Vector3.up * 50f;
                var accepted = BotOrders.MoveTo(bot, offMeshGoal);
                response = accepted
                    ? "Unexpectedly accepted the off-mesh probe; inspect botspike status and server log."
                    : "Issued deliberate y+50 off-mesh probe. Expected [BotOrders] OFF_MESH and FAIL summary are in the log.";
                return !accepted;
            }

            if (!TryParseRoom(target, out var room))
            {
                response = $"Unknown room '{target}'. Use an exact RoomName such as LczToilets, Lcz173, Lcz914, or preset.";
                return false;
            }

            BeginRoute(new[] { room }, room.ToString());
            response = $"Started walk to {room}.";
            return true;
        }

        public static bool Tour(out string response)
        {
            if (!EnsureBot(out response))
            {
                return false;
            }

            BeginRoute(TourRoute, "tour");
            response = $"Started tour: {string.Join(" -> ", TourRoute)}.";
            return true;
        }

        public static string Status()
        {
            if (bot == null || !BotManager.Instance.BotPlayers.ContainsKey(bot))
            {
                return "No spike bot. Run botspike start.";
            }

            var role = bot.roleManager?.CurrentRole?.RoleTypeId.ToString() ?? "none";
            if (!BotOrders.TryGetStatus(bot, out var status))
            {
                return $"bot={BotName} role={role} route={routeLabel} routeRunning={routeRunning} order=none pos={Format(bot.transform.position)}";
            }

            return $"bot={BotName} role={role} room={status.Room} route={routeLabel} routeRunning={routeRunning} order={status.CurrentOrder} active={status.IsActive} hasPath={status.HasPath} remaining={status.DistanceRemaining:F2} elapsed={status.ElapsedSeconds:F1} stalls={status.StallCount} doors={status.DoorsTraversed} maxTick={status.MaxTickDistance:F3} teleport={status.TeleportDetected} groundMisses={status.GroundProbeMisses} maxGround={status.MaxGroundDistance:F2} failure={status.FailureReason}";
        }

        public static bool Stop(out string response)
        {
            if (!EnsureBot(out response))
            {
                return false;
            }

            StopRoute();
            BotOrders.Stop(bot);
            routeLabel = "none";
            response = "Stopped the active route and left the bot holding position.";
            return true;
        }

        public static bool Cleanup(out string response)
        {
            StopRoute();
            if (bot == null)
            {
                response = "No spike bot to clean up.";
                return true;
            }

            var removed = BotOrders.DespawnBot(bot);
            bot = null;
            routeLabel = "none";
            response = removed ? "Despawned the spike bot." : "Spike bot was already gone.";
            return true;
        }

        private static void BeginRoute(IReadOnlyList<RoomName> rooms, string label)
        {
            StopRoute();
            routeLabel = label;
            routeRunning = true;
            routeHandle = Timing.RunCoroutine(RunRoute(rooms, label));
        }

        private static IEnumerator<float> RunRoute(IReadOnlyList<RoomName> rooms, string label)
        {
            var waitStarted = Time.time;
            while (bot != null
                && bot.roleManager?.CurrentRole is not PlayerRoles.FirstPersonControl.FpcStandardRoleBase
                && Time.time - waitStarted < 10f)
            {
                yield return Timing.WaitForSeconds(0.25f);
            }

            if (bot == null || bot.roleManager?.CurrentRole is not PlayerRoles.FirstPersonControl.FpcStandardRoleBase)
            {
                LabLogger.Error($"[BotOrders] ROUTE verdict=FAIL label={label} reason=fpc-role-timeout waited={Time.time - waitStarted:F1}");
                routeRunning = false;
                yield break;
            }

            var routeStarted = Time.time;
            var totalStalls = 0;
            var totalDoors = 0;
            var maxTick = 0f;
            var teleport = false;
            var groundMisses = 0;
            var maxGround = 0f;

            foreach (var room in rooms)
            {
                if (bot == null || !BotOrders.MoveToRoom(bot, room))
                {
                    LabLogger.Error($"[BotOrders] ROUTE verdict=FAIL label={label} room={room} reason=order-rejected");
                    routeRunning = false;
                    yield break;
                }

                while (bot != null && BotOrders.TryGetStatus(bot, out var activeStatus) && activeStatus.IsActive)
                {
                    if (activeStatus.ElapsedSeconds >= 120f)
                    {
                        BotOrders.Stop(bot);
                        LabLogger.Error($"[BotOrders] ROUTE verdict=FAIL label={label} room={room} reason=order-timeout elapsed={activeStatus.ElapsedSeconds:F1} stalls={activeStatus.StallCount}");
                        routeRunning = false;
                        yield break;
                    }

                    yield return Timing.WaitForSeconds(0.25f);
                }

                if (bot == null || !BotOrders.TryGetStatus(bot, out var status))
                {
                    LabLogger.Error($"[BotOrders] ROUTE verdict=FAIL label={label} room={room} reason=bot-or-status-lost");
                    routeRunning = false;
                    yield break;
                }

                totalStalls += status.StallCount;
                totalDoors += status.DoorsTraversed;
                maxTick = Mathf.Max(maxTick, status.MaxTickDistance);
                teleport |= status.TeleportDetected;
                groundMisses += status.GroundProbeMisses;
                maxGround = Mathf.Max(maxGround, status.MaxGroundDistance);

                if (status.CurrentOrder != BotOrderKind.Completed)
                {
                    LabLogger.Error($"[BotOrders] ROUTE verdict=FAIL label={label} room={room} terminal={status.CurrentOrder} reason={status.FailureReason}");
                    routeRunning = false;
                    yield break;
                }

                yield return Timing.WaitForSeconds(0.5f);
            }

            var verdict = teleport || maxTick > 1.5f || groundMisses > 0 ? "FAIL" : "PASS";
            LabLogger.Info($"[BotOrders] ROUTE verdict={verdict} label={label} rooms={rooms.Count} elapsed={Time.time - routeStarted:F2} stalls={totalStalls} doors={totalDoors} maxTick={maxTick:F3} teleport={teleport} groundMisses={groundMisses} maxGround={maxGround:F2}");
            routeRunning = false;
        }

        private static void StopRoute()
        {
            if (routeRunning)
            {
                Timing.KillCoroutines(routeHandle);
                routeRunning = false;
            }
        }

        private static bool EnsureBot(out string response)
        {
            if (bot == null || !BotManager.Instance.BotPlayers.ContainsKey(bot))
            {
                response = "No spike bot. Run botspike start first.";
                return false;
            }

            response = string.Empty;
            return true;
        }

        private static bool TryParseRoom(string value, out RoomName room)
        {
            if (Enum.TryParse(value, true, out room) && room != RoomName.Unnamed)
            {
                return true;
            }

            switch (value.ToLowerInvariant())
            {
                case "classd":
                case "spawn":
                    room = RoomName.LczClassDSpawn;
                    return true;
                case "toilets":
                    room = RoomName.LczToilets;
                    return true;
                case "173":
                    room = RoomName.Lcz173;
                    return true;
                case "914":
                    room = RoomName.Lcz914;
                    return true;
                default:
                    room = default;
                    return false;
            }
        }

        private static string BotName => bot?.nicknameSync?.MyNick ?? "none";
        private static string Format(Vector3 value) => $"({value.x:F2},{value.y:F2},{value.z:F2})";
    }
}
