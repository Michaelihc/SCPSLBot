using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UserSettings.ServerSpecific;

namespace ServerKeybinds;

internal static class SssViewFingerprint
{
    public static string Compute(IReadOnlyList<ServerSpecificSettingBase> settings)
    {
        StringBuilder canonical = new();
        foreach (ServerSpecificSettingBase setting in settings)
        {
            Append(canonical, setting.GetType().FullName);
            Append(canonical, setting.SettingId.ToString(CultureInfo.InvariantCulture));
            Append(canonical, setting.Label);
            Append(canonical, setting.HintDescription);
            Append(canonical, setting.CollectionId.ToString(CultureInfo.InvariantCulture));
            Append(canonical, setting.IsServerOnly ? "1" : "0");

            switch (setting)
            {
                case SSGroupHeader header:
                    Append(canonical, header.ReducedPadding ? "1" : "0");
                    break;
                case SSKeybindSetting keybind:
                    Append(canonical, ((int)keybind.SuggestedKey).ToString(CultureInfo.InvariantCulture));
                    Append(canonical, keybind.PreventInteractionOnGUI ? "1" : "0");
                    Append(canonical, keybind.AllowSpectatorTrigger ? "1" : "0");
                    break;
                case SSDropdownSetting dropdown:
                    Append(canonical, dropdown.DefaultOptionIndex.ToString(CultureInfo.InvariantCulture));
                    Append(canonical, ((int)dropdown.EntryType).ToString(CultureInfo.InvariantCulture));
                    foreach (string option in dropdown.Options)
                    {
                        Append(canonical, option);
                    }
                    break;
                case SSTwoButtonsSetting twoButtons:
                    Append(canonical, twoButtons.OptionA);
                    Append(canonical, twoButtons.OptionB);
                    Append(canonical, twoButtons.DefaultIsB ? "1" : "0");
                    break;
                case SSSliderSetting slider:
                    Append(canonical, slider.MinValue.ToString("R", CultureInfo.InvariantCulture));
                    Append(canonical, slider.MaxValue.ToString("R", CultureInfo.InvariantCulture));
                    Append(canonical, slider.DefaultValue.ToString("R", CultureInfo.InvariantCulture));
                    Append(canonical, slider.Integer ? "1" : "0");
                    Append(canonical, slider.ValueToStringFormat);
                    Append(canonical, slider.FinalDisplayFormat);
                    break;
                case SSTextArea text:
                    Append(canonical, ((int)text.Foldout).ToString(CultureInfo.InvariantCulture));
                    Append(canonical, ((int)text.AlignmentOptions).ToString(CultureInfo.InvariantCulture));
                    break;
                case SSPlaintextSetting plaintext:
                    Append(canonical, plaintext.Placeholder);
                    Append(canonical, plaintext.DefaultText);
                    Append(canonical, ((int)plaintext.ContentType).ToString(CultureInfo.InvariantCulture));
                    Append(canonical, plaintext.CharacterLimit.ToString(CultureInfo.InvariantCulture));
                    break;
                case SSButton button:
                    Append(canonical, button.ButtonText);
                    Append(canonical, button.HoldTimeSeconds.ToString("R", CultureInfo.InvariantCulture));
                    break;
            }
        }

        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
        StringBuilder hex = new(hash.Length * 2);
        foreach (byte value in hash)
        {
            hex.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }

    private static void Append(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('|');
    }
}
