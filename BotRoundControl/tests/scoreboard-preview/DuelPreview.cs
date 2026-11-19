using DisplayKit.Preview;

namespace DuelArena;

public static class DuelPreview
{
    [DisplayKitScenario("duel-gallery")]
    public static void Match(PreviewContext context)
    {
        var viewer = context.AddPlayer("Viewer");
        context.AddPlayer("Fighter");
        var panel = new DuelScoreboard(viewer.Hub);
        panel.Update("决斗场  /  对决中", "下一场  ·  阿墨 / 寒星", "月白", 100, 100, 2, "青松", 100, 100, 0);
        context.Frame("opening");
        panel.Update("决斗场  /  对决中", "下一场  ·  阿墨 / 寒星", "月白", 64, 100, 2, "青松", 28, 100, 0);
        context.Frame(1f, "health changed");
        panel.Destroy();
        context.Frame(1f, "left gallery");
    }
}
