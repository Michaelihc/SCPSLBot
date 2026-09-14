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
