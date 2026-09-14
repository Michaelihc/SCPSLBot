using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;
using System;

namespace SCPSLBot.Tests
{
    public class LabApiPlugin : Plugin
    {
        public override string Name { get; } = "SCPSLBot.Tests";
        public override string Description { get; } = "Testing plugin for bot players addon.";
        public override string Author { get; } = "repkins(19)";
        public override Version Version { get; } = new("0.0.1");
        public override Version RequiredApiVersion { get; } = new(LabApiProperties.CompiledVersion);

        public override void Enable()
        {
            Logger.Info("Enabled plugin.");
        }

        public override void Disable()
        {
            Logger.Info("Disabled plugin.");
        }
    }
}
