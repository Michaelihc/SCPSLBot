using System.Collections.Generic;
using InventorySystem.Items.Usables;
using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles;
using PlaytestHarness.Actors;
using PlaytestHarness.Core;

namespace WarmupSafezone.Playtests;

/// <summary>
/// Native action coverage for an action family with a reliable harness cancellation/success oracle.
/// The safezone policy deliberately permits movement/utility abilities, while offensive SCP actions
/// are denied by their role-specific pre-action events and final damage remains a backstop.
/// </summary>
public sealed class SafezoneActionScenario : Scenario
{
    public override string Name => "warmup-safezone-actions";
    public override string[] Aliases => ["safezone-actions"];
    public override string[] Suites => ["warmup-safezone"];
    public override string Description => "Proves that a real SCP utility ability remains usable inside SCP-914; see the transcript for negative-action harness gaps.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        Actor scp173 = ctx.SpawnActor(
            "safezone-scp173",
            RoleTypeId.Scp173,
            SpawnSpec.Native(),
            useMovementProvider: false);
        yield return scp173.WaitReady();
        yield return scp173.GoTo(RoomName.Lcz914);

        Player player = Player.Get(scp173.PlayerId)
            ?? throw new RequireException("SCP-173 action actor has no LabAPI player wrapper");
        ctx.Require(scp173.RoomName == nameof(RoomName.Lcz914),
            "SCP-173 did not settle inside the SCP-914 safezone");
        ctx.Require(!player.IsGodModeEnabled,
            "WarmupSafezone must not grant godmode while exercising native actions");

        // Breakneck is movement/utility, not an attack. This uses the harness's native
        // ServerProcessCmd adapter and requires both the cancellable pre-event and completed state
        // transition, proving the policy does not indiscriminately disable every SCP ability.
        yield return scp173.AttemptBreakneck(AbilityExpectedOutcome.Succeeded);
        ctx.Info("native SCP-173 Breakneck utility succeeded inside SCP-914 as allowed by the action policy");

        Actor scp244User = ctx.SpawnActor(
            "safezone-scp244-user",
            RoleTypeId.ClassD,
            SpawnSpec.Native(),
            useMovementProvider: false);
        yield return scp244User.WaitReady();
        yield return scp244User.GoTo(RoomName.Lcz914);
        ctx.Arrange("give an SCP-244 for native blocked-use coverage", () => scp244User.GiveItem(ItemType.SCP244a));
        yield return scp244User.Equip(ItemType.SCP244a);

        Player scp244Player = Player.Get(scp244User.PlayerId)
            ?? throw new RequireException("SCP-244 actor has no LabAPI player wrapper");

        InventorySystem.Items.Usables.Scp244.Scp244Item held =
            scp244Player.ReferenceHub.inventory.CurInstance as InventorySystem.Items.Usables.Scp244.Scp244Item
            ?? throw new RequireException("SCP-244 actor is not holding the native SCP-244 item");
        ushort serial = held.ItemSerial;
        UsableItemsController.ServerEmulateMessage(serial, StatusMessage.StatusType.Start);
        yield return ctx.Wait(0.3f);
        PlayerHandler handler = UsableItemsController.GetHandler(scp244Player.ReferenceHub);
        ctx.Require(handler.CurrentUsable.ItemSerial != serial,
            "SCP-244 native use started inside the SCP-914 safezone");
        ctx.Require(scp244Player.ReferenceHub.inventory.UserInventory.Items.ContainsKey(serial),
            "blocked SCP-244 use removed or deployed the held item");
        ctx.Info("native SCP-244 start was cancelled inside SCP-914 and the exact item remained held");
    }
}
