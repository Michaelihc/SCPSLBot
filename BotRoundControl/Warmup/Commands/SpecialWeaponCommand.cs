#nullable enable

using System;
using BotRoundControl;
using CommandSystem;
using LabApi.Features.Wrappers;
using SCPSLBot.Warmup.SpecialWeapons;

namespace SCPSLBot.Warmup.Commands;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
internal sealed class SpecialWeaponCommand : ICommand
{
    private const string Usage = "Use: special_weapon list | status | grant <weapon> [player id] | fire [player id]";

    public string Command => "special_weapon";

    public string[] Aliases => new[] { "sw" };

    public string Description => "Lists, grants or test-fires warmup special weapons.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.GivingItems, out response))
        {
            return false;
        }

        SpecialWeaponsRuntime? weapons = BotRoundControlPlugin.Instance?.SpecialWeapons;
        if (weapons == null)
        {
            response = "Special weapons are not running.";
            return false;
        }

        string action = arguments.Count > 0 ? arguments.At(0).ToLowerInvariant() : "list";
        switch (action)
        {
            case "list":
                response = "Weapons: " + string.Join(", ", weapons.WeaponIds);
                return true;
            case "status":
                response = weapons.DescribeState();
                return true;
            case "grant" when arguments.Count >= 2:
                return Grant(weapons, arguments, sender, out response);
            case "fire":
                return Fire(weapons, arguments, sender, out response);
            default:
                response = Usage;
                return false;
        }
    }

    private static bool Grant(SpecialWeaponsRuntime weapons, ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        string weaponId = arguments.At(1);
        if (!TryGetTarget(arguments, 2, sender, out Player? target, out response))
        {
            return false;
        }

        bool granted = weapons.TryGrant(target!, weaponId);
        response = granted
            ? $"Granted '{weaponId}' to {target!.Nickname}."
            : $"Could not grant '{weaponId}' to {target!.Nickname}. The weapon may be unknown or disabled, or the target ineligible.";
        return granted;
    }

    private static bool Fire(SpecialWeaponsRuntime weapons, ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!TryGetTarget(arguments, 1, sender, out Player? target, out response))
        {
            return false;
        }

        bool handled = weapons.PressPrimary(target!);
        response = handled
            ? $"Primary Use handled for {target!.Nickname}."
            : $"{target!.Nickname} has no key-fired weapon selected.";
        return handled;
    }

    private static bool TryGetTarget(ArraySegment<string> arguments, int index, ICommandSender sender, out Player? target, out string response)
    {
        response = string.Empty;
        if (arguments.Count > index)
        {
            if (int.TryParse(arguments.At(index), out int playerId) && Player.TryGet(playerId, out target) && target != null)
            {
                return true;
            }

            target = null;
            response = $"No connected player with id '{arguments.At(index)}'.";
            return false;
        }

        target = Player.Get(sender);
        if (target != null)
        {
            return true;
        }

        response = "Name a player id when using this from the server console.";
        return false;
    }
}
