# Plugin API

Other plugins can drive bots directly:

```csharp
using MapGeneration;
using PlayerRoles;
using SCPSLBot.AI;
using SCPSLBot.Api;

ReferenceHub bot = BotOrders.SpawnBot("Guard Bot", RoleTypeId.FacilityGuard);
BotOrders.MoveToRoom(bot, RoomName.HczArmory);   // or MoveTo(bot, worldPosition)
BotOrders.TryGetStatus(bot, out BotOrderStatus status);
BotOrders.Stop(bot);
BotOrders.DespawnBot(bot);

bool isBot = ManagedBotIdentity.IsManaged(player); // true for dummies SCPSLBot currently drives
bool botsOnly = BotsOnlyMode.IsEnabled;            // the configured bots_only switch
```

On runtime navigation, `BotOrders.MoveTo` accepts either a floor point or the actor's native
standing-root position.
