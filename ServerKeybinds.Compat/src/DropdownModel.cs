using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerKeybinds;

/// <summary>
/// The final, per-recipient view of a regular (non-scrollable) dropdown.
/// Return <see cref="Hidden"/> to omit the entry for that recipient.
/// Resolvers must be cheap and side-effect free: building or sending settings never executes an action.
/// </summary>
public sealed class DropdownModel
{
    private static readonly string[] EmptyOptions = { string.Empty };

    public DropdownModel(
        string label,
        IEnumerable<string> options,
        int defaultIndex = 0,
        string hint = "",
        bool visible = true)
    {
        Label = label ?? throw new ArgumentNullException(nameof(label));
        Hint = hint ?? string.Empty;
        Visible = visible;

        string[] copied = options?.ToArray() ?? throw new ArgumentNullException(nameof(options));
        if (visible && copied.Length == 0)
        {
            throw new ArgumentException("A visible dropdown must contain at least one option.", nameof(options));
        }

        if (copied.Length > byte.MaxValue)
        {
            throw new ArgumentException("SSS dropdowns support at most 255 options.", nameof(options));
        }

        Options = Array.AsReadOnly(copied.Length == 0 ? EmptyOptions : copied);
        DefaultIndex = Math.Max(0, Math.Min(defaultIndex, Options.Count - 1));
    }

    private DropdownModel()
    {
        Label = string.Empty;
        Hint = string.Empty;
        Options = Array.AsReadOnly(EmptyOptions);
        Visible = false;
    }

    /// <summary>A reusable model that omits the dropdown for the recipient.</summary>
    public static DropdownModel Hidden { get; } = new();

    public string Label { get; }

    public IReadOnlyList<string> Options { get; }

    public int DefaultIndex { get; }

    public string Hint { get; }

    public bool Visible { get; }
}

/// <summary>
/// A deliberate personalized-dropdown change validated against the exact model that was sent.
/// Consumers must still recheck gameplay authorization when executing the requested value.
/// </summary>
public readonly struct DropdownSelection
{
    public DropdownSelection(int index, string value, long sendGeneration)
    {
        Index = index;
        Value = value;
        SendGeneration = sendGeneration;
    }

    public int Index { get; }

    public string Value { get; }

    public long SendGeneration { get; }
}
