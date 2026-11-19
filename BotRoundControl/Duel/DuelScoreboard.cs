using System;
using DisplayKit.Elements;
using DisplayKit.Enums;
using UnityEngine;
using UnityEngine.UIElements;

namespace DuelArena;

internal sealed class DuelScoreboard
{
    private readonly DisplayCanvas canvas;
    private readonly DisplayText stage;
    private readonly FighterRow left;
    private readonly FighterRow right;
    private readonly DisplayText next;

    internal DuelScoreboard(ReferenceHub viewer)
    {
        canvas = DisplayCanvas.Create();
        try
        {
            canvas.SortOrder = 75;
            canvas.Flex.Grow = 1;
            var panel = canvas.AddElement();
            panel.Position.Position = Position.Absolute;
            panel.Position.Right = Length.Percent(3);
            panel.Position.Top = Length.Percent(15);
            panel.Size.Width = 258;
            panel.Size.MaxWidth = Length.Percent(35);
            panel.Background.Color = (Color)new Color32(16, 26, 36, 242);
            panel.Spacing.PaddingLeft = panel.Spacing.PaddingRight = 14;
            panel.Spacing.PaddingTop = panel.Spacing.PaddingBottom = 10;
            stage = Label(panel, 13, new Color32(195, 211, 220, 255));
            left = new FighterRow(panel, new Color32(98, 208, 227, 255));
            right = new FighterRow(panel, new Color32(255, 180, 95, 255));
            next = Label(panel, 13, new Color32(195, 211, 220, 255));
            next.Spacing.MarginTop = 8;
            canvas.Spawn(viewer);
        }
        catch
        {
            canvas.Destroy();
            throw;
        }
    }

    internal void Update(string phase, string nextMatch,
        string leftName, int leftHealth, int leftMax, int leftWins,
        string rightName, int rightHealth, int rightMax, int rightWins)
    {
        stage.Content = phase;
        next.Content = nextMatch;
        left.Set(leftName, leftHealth, leftMax, leftWins);
        right.Set(rightName, rightHealth, rightMax, rightWins);
    }

    internal void Destroy()
    {
        canvas.Hide();
        canvas.Destroy();
    }

    private static DisplayText Label(DisplayElement parent, int size, Color color)
    {
        var label = parent.AddText("");
        label.Text.Color = color;
        label.Text.Font = FontType.RobotoRegular;
        label.Text.FontSize = size;
        label.Text.Wrap = WhiteSpace.Normal;
        return label;
    }

    private sealed class FighterRow
    {
        private readonly DisplayText name;
        private readonly DisplayText health;
        private readonly DisplayElement fill;

        internal FighterRow(DisplayElement panel, Color color)
        {
            var row = panel.AddElement();
            row.Spacing.MarginTop = 7;
            row.Flex.Direction = FlexDirection.Row;
            row.Align.AlignItems = Align.Center;
            name = Label(row, 16, Color.white);
            name.Flex.Grow = 1;
            health = Label(row, 13, color);
            health.Flex.Shrink = 0;
            var track = panel.AddElement();
            track.Size.Height = 4;
            track.Background.Color = (Color)new Color32(62, 75, 86, 255);
            fill = track.AddElement();
            fill.Size.Height = 4;
            fill.Background.Color = color;
        }

        internal void Set(string fighter, int hp, int max, int wins)
        {
            name.Content = fighter + (wins > 0 ? $" · {wins}连胜" : "");
            health.Content = max > 0 ? $"{hp} HP" : "";
            fill.Size.Width = Length.Percent(max > 0 ? Mathf.Clamp01((float)hp / max) * 100 : 0);
        }
    }
}
