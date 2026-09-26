using System;
using CommandSystem;
using LabApi.Features.Enums;
using LabApi.Features.Wrappers;
using UnityEngine;

namespace ScpslPluginStarter.Commands;

// TEMPORARY diagnostic for the invisible SCP-914 panel text; removed before merge.
[CommandHandler(typeof(RemoteAdminCommandHandler))]
[CommandHandler(typeof(GameConsoleCommandHandler))]
public sealed class SafezoneDebugTextCommand : ICommand
{
    public string Command => "safezonedebugtext";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "Temporary: spawn text variants on the corridor side of the SCP-914 gate.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        Door? gate = Door.Get(DoorName.Lcz914Gate);
        if (gate == null)
        {
            response = "no gate";
            return false;
        }

        Transform t = gate.Transform;
        Quaternion facing = t.rotation * Quaternion.Euler(0f, 180f, 0f);
        Vector3 origin = t.position + t.forward * 0.8f;
        Spawn(origin + t.right * -2.2f + Vector3.up * 1.2f, facing, "安全区", 0.32f, new Vector2(80f, 4f));
        Spawn(origin + Vector3.up * 1.8f, facing, "安全区\n禁止造成或受到伤害", 0.12f, new Vector2(60f, 24f));
        Spawn(origin + t.right * 2.2f + Vector3.up * 2.4f, facing, "安全区\n禁止造成或受到伤害", 0.32f, new Vector2(80f, 12f));
        response = FormattableString.Invariant($"spawned 3 variants at {origin} forward={t.forward}");
        return true;
    }

    private static void Spawn(Vector3 position, Quaternion rotation, string text, float scale, Vector2 size)
    {
        TextToy label = TextToy.Create(position, rotation, new Vector3(scale, scale, scale), null, false);
        label.TextFormat = $"<alpha=#FF><align=center><b><color=#42F5E9>{text}</color></b></align>";
        label.DisplaySize = size;
        label.IsStatic = true;
        label.SyncInterval = 0f;
        label.Spawn();
    }
}
