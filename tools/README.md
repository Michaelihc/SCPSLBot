# SCPSLBot maintenance tools

## Admin exemption patch

`New-SCPSLBotAdminExemption.ps1` applies a narrowly scoped IL patch to the exact
deployed `SCPSLBot.dll` whose source is not present in this checkout. It exempts
players with LabAPI `RemoteAdminAccess` from:

- the role-changing event rewrite;
- the delayed arena-role rewrite; and
- the recurring arena role and position enforcement loop.

The script refuses any input whose SHA-256 is not
`08baa0ee8f11b42c542bee2a7a9c6ed5104388058f31f4eaabfcda7cc3d3c491`, refuses
to overwrite an output file, and verifies all three inserted checks after writing.
It only creates a local DLL; it does not upload, deploy, or restart anything.

The tool uses the `Mono.Cecil.dll` bundled with the locally installed `ilspycmd`
dotnet tool. A different Cecil assembly can be supplied with `-CecilPath`.

```powershell
./tools/New-SCPSLBotAdminExemption.ps1 `
    -InputPath ./SCPSLBot.original.dll `
    -OutputPath ./SCPSLBot.dll `
    -ReferenceDirectory ./managed-assemblies
```

`ReferenceDirectory` must contain the matching server assemblies needed to write
the deployed plugin, including `Assembly-CSharp.dll` and `LabApi.dll`. It can be
omitted when those assemblies are beside the input DLL.


## Runtime navmesh asset patcher

`NavMeshAssetPatcher/` is a .NET 8 console (dependency: `AssetsTools.NET` 3.0.5 plus the vendored
UABE `classdata.tpk`; the plugin itself gains no dependency). It makes every `Mesh` asset of the
dedicated server readable and inlines the vertex data that a player build streams from `.resS`
side files, because Unity's runtime navmesh builder keeps no CPU copy of streamed vertex data even
when the mesh is flagged readable.

```powershell
dotnet build .\tools\NavMeshAssetPatcher\NavMeshAssetPatcher.csproj -c Release
$patcher = '.\tools\NavMeshAssetPatcher\bin\Release\net8.0\NavMeshAssetPatcher.dll'
dotnet $patcher report  --server "<server root>"   # read-only: meshes / unreadable / streamed per file
dotnet $patcher patch   --server "<server root>"   # rewrite atomically, keep .bak, write navmesh-asset-patch.json
dotnet $patcher verify  --server "<server root>"   # exit 0 patched, 2 drifted/unpatched, 1 error
dotnet $patcher restore --server "<server root>"   # put the .bak originals back
dotnet $patcher dump    --server "<server root>" --name <mesh name>   # serialized layout of one mesh
.\tools\Publish-NavMeshAssetPatcher.ps1           # self-contained win-x64 and linux-x64 binaries
```

The server root is the folder containing `SCPSL_Data` (`SCPSL_SERVER_ROOT` is used when `--server`
is omitted). `verify` is wired into `Start-BotTestServer8888.ps1`, the isolated 8891 drivers under
`tests/playtest/tools/` and `Deploy-BotProduction7777.sh`. Re-apply after every game update; the
manifest records the `Assembly-CSharp.dll` hash it was made for.
