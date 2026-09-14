using System;
using System.Linq;
using CommandSystem;
using LabApi.Features.Wrappers;
using StatsBots.Config;
using StatsBots.Integration;

namespace StatsBots.Commands;

[CommandHandler(typeof(ClientCommandHandler))]
internal sealed class WarmupTitleCommand : ICommand
{
    public string Command => "warmuptitle";
    public string[] Aliases => new[] { "wtitle" };
    public string Description => "List or select an unlocked warmup title.";
    public bool SanitizeResponse => false;

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        var runtime = StatsBotsPlugin.Instance?.Runtime;
        Player? player = Player.Get(sender);
        if (player == null)
        {
            response = "StatsBots is unavailable.";
            return false;
        }
        if (runtime == null)
        {
            bool english = string.Equals(StatsBotsPlugin.Instance?.Config.Language?.Trim(), "en", StringComparison.OrdinalIgnoreCase);
            response = english ? "StatsBots is unavailable." : "StatsBots 当前不可用。";
            return false;
        }
        if (arguments.Count == 0 || string.Equals(arguments.At(0), "list", StringComparison.OrdinalIgnoreCase))
        {
            ProviderState state = runtime.TryGetUnlockedTitles(player, out var unlocked, out long selected);
            if (state != ProviderState.Ready)
            {
                response = runtime.Localize(player,
                    "Warmup stats are loading or unavailable; unlocked titles cannot be listed yet.",
                    "热身数据正在加载或不可用，暂时无法列出已解锁称号。" );
                return false;
            }
            string listed = string.Join(", ", unlocked.Select(title => title.Id + (title.Code == selected ? "*" : string.Empty)));
            response = runtime.Localize(player,
                "Unlocked titles: none" + (listed.Length > 0 ? ", " + listed : string.Empty),
                "已解锁称号：无" + (listed.Length > 0 ? "，" + listed : string.Empty));
            return true;
        }
        return runtime.TrySelect(player, arguments.At(0), out response) == ProviderState.Ready;
    }
}
