# Plugin API

Other plugins can drive bots directly:

```csharp
using MapGeneration;
using PlayerRoles;
using SCPSLBot.AI;
using SCPSLBot.Api;

ReferenceHub bot = BotOrders.SpawnBot("Guard Bot", RoleTypeId.FacilityGuard);
bool accepted = BotOrders.MoveToRoom(bot, RoomName.HczArmory);   // or MoveTo(bot, worldPosition)
BotOrders.TryGetStatus(bot, out BotOrderStatus status);          // CurrentOrder, IsActive, FailureReason
BotOrders.Stop(bot);
BotOrders.DespawnBot(bot);

bool isBot = ManagedBotIdentity.IsManaged(player); // true for dummies SCPSLBot currently drives
bool botsOnly = BotsOnlyMode.IsEnabled;            // the configured bots_only switch
```

## Orders

- A bot with an order walks without fighting: it ignores enemies and damage until the order ends.
- Every order ends passive. After `Completed`, `Stopped` or a failure the bot holds position and
  does not fight or return to its normal AI; a new order moves it again.
- Until its first order a bot runs its normal AI and attacks enemies. To keep a bot passive from
  the start, order it, or call `Stop`, in the same tick as `SpawnBot`.
- A rejected order returns `false`, leaves the bot passive and records `FailureReason`.
- `MoveToRoom` on a bot that has not spawned yet returns `true` and is resolved when it spawns.
  Check `TryGetStatus` for a failure from that point on.
- `MoveToRoom` goes to the nearest point of the room the bot can reach, which can be the near side
  of a door it cannot open. Bots only plan through doors their role or carried keycards open.
- `MoveTo` to a point the bot cannot reach fails with `FailedNoPath` shortly after it starts moving.
- A room needs navigation for `MoveToRoom`. On the default `Runtime` backend that takes the
  [patched server assets](navigation.md#choosing-a-backend); otherwise use `Authored`.
- On runtime navigation, `MoveTo` accepts either a floor point or the actor's native standing-root
  position.
