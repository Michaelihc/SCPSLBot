using System;

namespace ServerKeybinds;

/// <summary>
/// The final per-recipient view of a native SSS button. Return <see cref="Hidden"/> to omit it.
/// The resolver is presentation-only; the press callback must still revalidate live authority.
/// </summary>
public sealed class ButtonModel
{
    public ButtonModel(
        string label,
        string buttonText,
        float holdTimeSeconds = 0f,
        string hint = "",
        bool visible = true)
    {
        Label = label ?? throw new ArgumentNullException(nameof(label));
        ButtonText = buttonText ?? throw new ArgumentNullException(nameof(buttonText));
        HoldTimeSeconds = Math.Max(0f, holdTimeSeconds);
        Hint = hint ?? string.Empty;
        Visible = visible;
    }

    private ButtonModel()
    {
        Label = string.Empty;
        ButtonText = string.Empty;
        Hint = string.Empty;
        Visible = false;
    }

    public static ButtonModel Hidden { get; } = new();

    public string Label { get; }

    public string ButtonText { get; }

    public float HoldTimeSeconds { get; }

    public string Hint { get; }

    public bool Visible { get; }
}
