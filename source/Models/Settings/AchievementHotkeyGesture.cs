using System;
using System.Globalization;
using System.Linq;
using System.Windows.Input;

namespace PlayniteAchievements.Models.Settings
{
    public sealed class AchievementHotkeyGesture : IEquatable<AchievementHotkeyGesture>
    {
        public static readonly AchievementHotkeyGesture Empty =
            new AchievementHotkeyGesture(Key.None, ModifierKeys.None);

        private AchievementHotkeyGesture(Key key, ModifierKeys modifiers)
        {
            Key = NormalizeKey(key);
            Modifiers = NormalizeModifiers(modifiers);
        }

        public Key Key { get; }

        public ModifierKeys Modifiers { get; }

        public bool IsEmpty => Key == Key.None;

        public bool CanRegisterGlobally => !IsEmpty &&
            (Modifiers != ModifierKeys.None || IsFunctionKey(Key));

        public static AchievementHotkeyGesture FromInput(Key key, ModifierKeys modifiers)
        {
            return new AchievementHotkeyGesture(key, modifiers);
        }

        public static bool TryCreate(Key key, ModifierKeys modifiers, out AchievementHotkeyGesture gesture)
        {
            gesture = null;

            var normalizedKey = NormalizeKey(key);
            var normalizedModifiers = NormalizeModifiers(modifiers);
            if (!IsSupportedKey(normalizedKey))
            {
                return false;
            }

            if (normalizedModifiers == ModifierKeys.None &&
                !IsLetterKey(normalizedKey) &&
                !IsDigitKey(normalizedKey) &&
                !IsFunctionKey(normalizedKey))
            {
                return false;
            }

            gesture = new AchievementHotkeyGesture(normalizedKey, normalizedModifiers);
            return true;
        }

        public static bool TryParse(string text, out AchievementHotkeyGesture gesture)
        {
            gesture = Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            var modifiers = ModifierKeys.None;
            Key? key = null;
            var parts = text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToList();

            if (parts.Count == 0)
            {
                return true;
            }

            foreach (var part in parts)
            {
                if (TryParseModifier(part, out var modifier))
                {
                    modifiers |= modifier;
                    continue;
                }

                if (key.HasValue || !TryParseKey(part, out var parsedKey))
                {
                    gesture = null;
                    return false;
                }

                key = parsedKey;
            }

            if (!key.HasValue)
            {
                gesture = null;
                return false;
            }

            return TryCreate(key.Value, modifiers, out gesture);
        }

        public static string NormalizeText(string text)
        {
            return TryParse(text, out var gesture) && gesture != null
                ? gesture.ToString()
                : string.Empty;
        }

        public static Key NormalizeKey(Key key)
        {
            return key == Key.System ? Key.None : key;
        }

        public static bool IsSupportedKey(Key key)
        {
            if (key == Key.None ||
                key == Key.System ||
                key == Key.ImeProcessed ||
                key == Key.DeadCharProcessed)
            {
                return false;
            }

            return !IsModifierKey(key);
        }

        public static bool IsLetterKey(Key key)
        {
            return key >= Key.A && key <= Key.Z;
        }

        public static bool IsDigitKey(Key key)
        {
            return (key >= Key.D0 && key <= Key.D9) ||
                   (key >= Key.NumPad0 && key <= Key.NumPad9);
        }

        public static bool IsFunctionKey(Key key)
        {
            return key >= Key.F1 && key <= Key.F24;
        }

        public bool Equals(AchievementHotkeyGesture other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }

            return Key == other.Key && Modifiers == other.Modifiers;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as AchievementHotkeyGesture);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Key * 397) ^ (int)Modifiers;
            }
        }

        public override string ToString()
        {
            if (IsEmpty)
            {
                return string.Empty;
            }

            var parts = new System.Collections.Generic.List<string>();
            if (Modifiers.HasFlag(ModifierKeys.Control))
            {
                parts.Add("Ctrl");
            }
            if (Modifiers.HasFlag(ModifierKeys.Alt))
            {
                parts.Add("Alt");
            }
            if (Modifiers.HasFlag(ModifierKeys.Shift))
            {
                parts.Add("Shift");
            }
            if (Modifiers.HasFlag(ModifierKeys.Windows))
            {
                parts.Add("Win");
            }

            parts.Add(GetKeyDisplayName(Key));
            return string.Join("+", parts);
        }

        private static ModifierKeys NormalizeModifiers(ModifierKeys modifiers)
        {
            return modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);
        }

        private static bool TryParseModifier(string text, out ModifierKeys modifier)
        {
            modifier = ModifierKeys.None;
            switch (text.Trim().ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifier = ModifierKeys.Control;
                    return true;
                case "ALT":
                    modifier = ModifierKeys.Alt;
                    return true;
                case "SHIFT":
                    modifier = ModifierKeys.Shift;
                    return true;
                case "WIN":
                case "WINDOWS":
                    modifier = ModifierKeys.Windows;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryParseKey(string text, out Key key)
        {
            key = Key.None;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var value = text.Trim();
            if (value.Length == 1)
            {
                var ch = value[0];
                if (ch >= 'A' && ch <= 'Z')
                {
                    key = (Key)Enum.Parse(typeof(Key), value);
                    return true;
                }

                if (ch >= 'a' && ch <= 'z')
                {
                    key = (Key)Enum.Parse(typeof(Key), char.ToUpperInvariant(ch).ToString());
                    return true;
                }

                if (ch >= '0' && ch <= '9')
                {
                    key = (Key)Enum.Parse(typeof(Key), "D" + ch);
                    return true;
                }
            }

            if (value.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) &&
                value.Length == 7 &&
                char.IsDigit(value[6]))
            {
                key = (Key)Enum.Parse(typeof(Key), "NumPad" + value[6]);
                return true;
            }

            if (value.StartsWith("D", StringComparison.OrdinalIgnoreCase) &&
                value.Length == 2 &&
                char.IsDigit(value[1]))
            {
                key = (Key)Enum.Parse(typeof(Key), "D" + value[1]);
                return true;
            }

            return Enum.TryParse(value, ignoreCase: true, out key) && IsSupportedKey(key);
        }

        private static bool IsModifierKey(Key key)
        {
            return key == Key.LeftCtrl ||
                   key == Key.RightCtrl ||
                   key == Key.LeftAlt ||
                   key == Key.RightAlt ||
                   key == Key.LeftShift ||
                   key == Key.RightShift ||
                   key == Key.LWin ||
                   key == Key.RWin;
        }

        private static string GetKeyDisplayName(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9)
            {
                return ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture);
            }

            return key.ToString();
        }
    }
}
