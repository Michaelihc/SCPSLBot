# ServerKeybinds compatibility fork / ServerKeybinds 兼容分支

[English](#english) | [中文](#中文)

## English

This directory is an additive compatibility fork of the process-local Server-Specific Settings owner.
It was copied from upstream `../ServerKeybinds` commit
`6c2229e0b707347604f441e51c4790bca6ad3a07` (`R1 keybind delivery diagnostics + latch/dictionary hygiene`).
The upstream directory is intentionally unchanged.

The fork retains the upstream namespace, public API, target (`net48`), and output assembly name
`ServerKeybinds`. API 4 adds:

- personalized regular dropdowns whose label, options, default, hint, and visibility are resolved per player;
- personalized native buttons whose label, caption, hold time, hint, and visibility are resolved per player;
- a send-generation acquisition baseline, so opening, acquiring, or refreshing a personalized action dropdown never invokes its callback;
- final per-player view fingerprints;
- `SssInterestIndex<TKey>` for personal and population-boundary routing;
- `SssRefreshCoordinator<TKey, TSnapshot>` with trailing 500 ms debounce, latest-snapshot replacement,
  reason coalescing, a two-second minimum interval, six sends per rolling minute, and identical-view suppression;
- per-player fingerprint/send diagnostics and process counters.

### Personalized dropdown API

```csharp
KeybindBlock block = KeybindRegistry.ClaimBlock(SssIdBlocks.ScpslBotWarmup, "SCPSLBot Warmup")
    .InCategory(SettingsCategory.Gameplay)
    .Header("Warmup")
    .AddDropdownForPlayer(
        1,
        player => new DropdownModel(
            "Respawn as",
            EligibleRoleIds(player),
            defaultIndex: 0,
            hint: "Applies immediately after a deliberate change."),
        (player, selection) =>
        {
            // Treat selection.Value as untrusted: recheck current authority and exact eligibility here.
            ApplyExactRoleOrReject(player, selection.Value);
            KeybindRegistry.RequestPlayerRefresh(player, "role-action");
        });
block.Enable();
```

`DropdownSelection` contains `Index`, `Value`, and `SendGeneration`. The first received value after each
successful personalized send becomes the baseline and is swallowed. Repeated baseline values are swallowed.
Only a later in-range value change, still present at the same index/value in the current model, invokes the
callback. `DropdownModel.Hidden` omits the entry. Resolvers build presentation only and must have no side effects.

Consumers remain responsible for rechecking server-authoritative permissions, role/zone eligibility, cooldowns,
and other gameplay rules at execution. A menu is not authority.

`AddButtonForPlayer` builds a per-player `ButtonModel` and invokes its callback only for a currently visible
native `SSButton`. A button response carries no client-selected value. When a workflow uses a dropdown plus
an Apply/Grant button, stage the dropdown's stable value on the server and execute only that staged value from
the button callback after revalidation. Pass a staging-only `onAcquired` callback to `AddDropdownForPlayer`
when the client's persisted, visibly selected acquisition value must also become the pending server value;
the acquisition callback must never perform the gameplay action itself.

The legacy fixed `AddDropdown` overload retains its API-3 callback behavior for existing consumers. Do not use
that legacy overload for a new action control; use `AddDropdownForPlayer`, whose acquisition guard is part of its
contract. Existing two-button and slider APIs likewise retain their upstream response behavior.

Reserved blocks introduced by this fork:

| Constant | Base | Owner / category |
| --- | ---: | --- |
| `SssIdBlocks.ScpslBotWarmup` | 1130000 | SCPSLBot personal warmup controls / Gameplay |
| `SssIdBlocks.StatsBots` | 1131000 | StatsBots display and title controls |
| `SssIdBlocks.ScpslBotTools` | 1132000 | SCPSLBot diagnostics, navigation, and force-role controls / Tools |

### Refresh and interest rules

Use `KeybindRegistry.RequestPlayerRefresh(player, reason)` after an event changes that player's visible view.
The legacy `RefreshPlayer(Player)` member remains source/binary compatible and now enters the same budget.
For explicit interest routing, use `SetPlayerInterests`, `InvalidatePlayer`, and
`InvalidatePopulationBoundary`. Population routing returns only the former sole player on 1→2 and only the
remaining player on 2→1; changes above two do not fan out.

### Packaging rule (mandatory)

The fork and upstream both produce an unsigned assembly named `ServerKeybinds.dll`. They are alternatives,
not two dependencies. A server/package must contain **exactly one** `ServerKeybinds.dll`: this API 4 fork when
any consumer uses personalized dropdown or refresh APIs. Never deploy it beside the upstream build, never rename
the DLL, and install the selected DLL once under LabAPI's `dependencies/<port>` directory. Keep
`dependencies/global` empty on local multi-lane deployments so one port cannot replace another port's ABI. The
project deliberately does not auto-deploy.

Pure policy checks:

```powershell
dotnet run --project .\tests\ServerKeybinds.Compat.PureTests\ServerKeybinds.Compat.PureTests.csproj -c Release
```

Build the dependency:

```powershell
dotnet build .\ServerKeybinds.csproj -c Release
```

## 中文

此目录是单个服务器进程内服务器专属设置（SSS）所有者的兼容分支。它复制自上游 `../ServerKeybinds` 的精确提交
`6c2229e0b707347604f441e51c4790bca6ad3a07`，并且不修改上游目录。

兼容分支保留原命名空间、公共 API、`net48` 目标与输出名 `ServerKeybinds.dll`。API 4 新增按玩家生成的
普通（不可滚动）下拉框与原生按钮、每次发送代际的首次值基线、最终视图指纹、兴趣路由，以及统一的刷新协调器。
协调器采用 500 毫秒尾随防抖、最新快照替换、原因合并、同一玩家两次发送至少间隔 2 秒、滚动 60 秒内
最多 6 次发送，并抑制相同指纹。获取、打开或刷新个性化动作下拉框不会执行操作；只有后续有意变更才会回调。

调用 `AddDropdownForPlayer` 创建控件，返回 `DropdownModel.Hidden` 可对该玩家隐藏。回调接收带有 `Index`、
`Value`、`SendGeneration` 的 `DropdownSelection`。执行时必须重新检查服务器端权限、角色/区域资格、冷却等规则；
菜单内容本身不是授权。

`AddButtonForPlayer` 会按玩家生成 `ButtonModel`，并且只在当前原生 `SSButton` 可见时执行回调。按钮响应
不携带客户端选择值；下拉框与应用/发放按钮组合使用时，应先在服务器暂存稳定值，再由按钮回调重新校验并执行。
如果客户端持久化且界面可见的首次值也必须成为待处理选择，可传入仅用于暂存的 `onAcquired` 回调；该回调绝不能执行玩法操作。

本分支保留三个不冲突的固定块：SCPSLBot 暖身玩法控件 `1130000`、StatsBots `1131000`、SCPSLBot
管理工具 `1132000`。

打包时必须只放一个名为 `ServerKeybinds.dll` 的程序集。上游版本与本兼容分支不能同时部署；使用个性化下拉框
或刷新 API 时应选择本 API 4 构建，并仅安装到 LabAPI 的 `dependencies/<端口>`。本地多端口部署应保持
`dependencies/global` 为空，避免一个端口替换另一个端口的 ABI。请勿重命名 DLL。本项目不会自动部署。
