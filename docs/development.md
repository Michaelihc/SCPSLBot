# Build and verify

```powershell
$env:SL_REFERENCES = 'C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed'
dotnet build SCPSLBotAddon.sln -c Release -p:Platform=x64 -p:DeployToLocalServer=false
dotnet test SCPSLBot.PolicyTests\SCPSLBot.PolicyTests.csproj -c Release
node ..\.tests\lint-scenarios.js
```

Build notes:

- `ServerKeybinds` and `HsmAdapter` are project references, resolved from sibling checkouts.
  Override them with `-p:ServerKeybindsProject=<path>` and `-p:HsmAdapterProject=<path>`.
- Build deployment is opt-in. The production solution excludes the in-server test and reload plugins.

Verification:

- The navigation gates boot the isolated port 8891 against the patched server assets, and every
  driver verifies the asset patch first:
  - `python tests/playtest/tools/check_connector_survey.py --scenario scpslbot-runtime-navmesh-gate`
  - `--scenario scpslbot-keycard-routing-survey`
  - `--rounds 2`
  - `--scenario scpslbot-door-survey`

  See [tests/playtest/README.md](../tests/playtest/README.md).
- The dedicated local bot-testing deployment is port `8888`. Start it with
  `tools\Start-BotTestServer8888.ps1`.
