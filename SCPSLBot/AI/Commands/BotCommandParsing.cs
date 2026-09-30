#nullable enable

using SCPSLBot.AI.FirstPersonControl.Objectives;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SCPSLBot.AI.Commands
{
    internal enum BotOrderVerb
    {
        MoveTo,
        Hold,
        Release,
        Objective,
    }

    internal readonly struct BotOrderRequest
    {
        public BotOrderRequest(bool allBots, int playerId, BotOrderVerb verb, float x, float y, float z, float engageRadius)
        {
            AllBots = allBots;
            PlayerId = playerId;
            Verb = verb;
            X = x;
            Y = y;
            Z = z;
            EngageRadius = engageRadius;
        }

        public bool AllBots { get; }
        public int PlayerId { get; }
        public BotOrderVerb Verb { get; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public float EngageRadius { get; }
    }

    /// <summary>Argument parsing for <c>bot_order</c> and <c>bot_add</c>. Pure so the grammar is unit-testable.</summary>
    internal static class BotCommandParsing
    {
        public const int MaxBulkAddCount = 40;

        public const string OrderUsage =
            "Usage: bot_order <playerId|all> moveto <x> <y> <z> | hold | release | objective <x> <y> <z> <radius>";

        public const string AddUsage = "Usage: bot_add [count] [role]";

        public static bool TryParseOrder(IReadOnlyList<string>? arguments, out BotOrderRequest request, out string error)
        {
            request = default;
            error = OrderUsage;
            if (arguments == null || arguments.Count < 2)
            {
                return false;
            }

            var allBots = string.Equals(arguments[0], "all", StringComparison.OrdinalIgnoreCase);
            var playerId = 0;
            if (!allBots && (!int.TryParse(arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out playerId) || playerId <= 0))
            {
                error = $"Invalid target '{arguments[0]}': expected a player ID or 'all'. {OrderUsage}";
                return false;
            }

            float x = 0f, y = 0f, z = 0f, radius = 0f;
            BotOrderVerb verb;
            switch (arguments[1].ToLowerInvariant())
            {
                case "moveto":
                    verb = BotOrderVerb.MoveTo;
                    if (arguments.Count != 5 || !TryParsePoint(arguments, 2, out x, out y, out z))
                    {
                        return false;
                    }

                    break;
                case "hold":
                    verb = BotOrderVerb.Hold;
                    if (arguments.Count != 2)
                    {
                        return false;
                    }

                    break;
                case "release":
                    verb = BotOrderVerb.Release;
                    if (arguments.Count != 2)
                    {
                        return false;
                    }

                    break;
                case "objective":
                    verb = BotOrderVerb.Objective;
                    if (arguments.Count != 6
                        || !TryParsePoint(arguments, 2, out x, out y, out z)
                        || !TryParseFinite(arguments[5], out radius))
                    {
                        return false;
                    }

                    if (radius < 0f || radius > BotObjectivePolicy.MaxEngageRadius)
                    {
                        error = $"Engage radius must be 0-{BotObjectivePolicy.MaxEngageRadius:F0} meters.";
                        return false;
                    }

                    break;
                default:
                    error = $"Unknown order '{arguments[1]}'. {OrderUsage}";
                    return false;
            }

            request = new BotOrderRequest(allBots, playerId, verb, x, y, z, radius);
            error = string.Empty;
            return true;
        }

        /// <summary>
        /// No arguments keeps the single capped <c>bot_add</c>; an explicit count (1-<see cref="MaxBulkAddCount"/>)
        /// spawns that many bots with an optional role token, resolved by the caller.
        /// </summary>
        public static bool TryParseAdd(IReadOnlyList<string>? arguments, out int count, out string? roleToken, out string error)
        {
            count = 0;
            roleToken = null;
            error = AddUsage;
            if (arguments == null || arguments.Count == 0)
            {
                error = string.Empty;
                return true;
            }

            if (arguments.Count > 2
                || !int.TryParse(arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
            {
                count = 0;
                return false;
            }

            if (count < 1 || count > MaxBulkAddCount)
            {
                error = $"Count must be 1-{MaxBulkAddCount}. {AddUsage}";
                count = 0;
                return false;
            }

            roleToken = arguments.Count == 2 ? arguments[1] : null;
            error = string.Empty;
            return true;
        }

        private static bool TryParsePoint(IReadOnlyList<string> arguments, int offset, out float x, out float y, out float z)
        {
            y = z = 0f;
            return TryParseFinite(arguments[offset], out x)
                   && TryParseFinite(arguments[offset + 1], out y)
                   && TryParseFinite(arguments[offset + 2], out z);
        }

        private static bool TryParseFinite(string value, out float result)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                   && !float.IsNaN(result)
                   && !float.IsInfinity(result);
        }
    }
}
