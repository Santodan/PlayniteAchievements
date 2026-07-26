using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.Overrides
{
    /// <summary>
    /// Declares how a provider exposes its per-game override to the UI: the value kind,
    /// the input label, the available choices, and value normalization/validation. Providers
    /// build a descriptor (capturing their own validation helpers) and expose it via
    /// <see cref="IProviderOverride.OverrideDescriptor"/>, replacing the former per-provider
    /// switch statements in the override UI.
    /// </summary>
    public sealed class ProviderOverrideDescriptor
    {
        private readonly Func<string, ProviderOverrideValidation> _validate;

        private ProviderOverrideDescriptor(
            ProviderOverrideValueKind valueKind,
            string inputLabelKey,
            bool valueOptional,
            IReadOnlyList<ProviderOverrideChoice> choices,
            Func<string, ProviderOverrideValidation> validate)
        {
            ValueKind = valueKind;
            InputLabelKey = inputLabelKey;
            ValueOptional = valueOptional;
            Choices = choices ?? Array.Empty<ProviderOverrideChoice>();
            _validate = validate ?? (_ => ProviderOverrideValidation.Valid(null));
        }

        public ProviderOverrideValueKind ValueKind { get; }

        public string InputLabelKey { get; }

        /// <summary>
        /// For <see cref="ProviderOverrideValueKind.Text"/>, whether an empty value is acceptable
        /// (the provider auto-detects, e.g. Exophase). Always true for <see cref="ProviderOverrideValueKind.None"/>.
        /// </summary>
        public bool ValueOptional { get; }

        public IReadOnlyList<ProviderOverrideChoice> Choices { get; }

        public ProviderOverrideValidation Validate(string rawValue) => _validate(rawValue);

        /// <summary>
        /// Maps a persisted value to its display label (choice label for Choice kind; the value itself otherwise).
        /// </summary>
        public string GetValueDisplay(string value)
        {
            if (ValueKind == ProviderOverrideValueKind.Choice)
            {
                var match = Choices.FirstOrDefault(choice =>
                    string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return match.DisplayName;
                }
            }

            return value;
        }

        /// <summary>Presence-only override with no value (e.g. FFXIV); always valid, persists null.</summary>
        public static ProviderOverrideDescriptor None()
            => new ProviderOverrideDescriptor(
                ProviderOverrideValueKind.None,
                inputLabelKey: null,
                valueOptional: true,
                choices: null,
                validate: _ => ProviderOverrideValidation.Valid(null));

        /// <summary>Free-text identifier/slug override with provider-supplied validation.</summary>
        public static ProviderOverrideDescriptor Text(
            string inputLabelKey,
            Func<string, ProviderOverrideValidation> validate,
            bool valueOptional = false)
            => new ProviderOverrideDescriptor(
                ProviderOverrideValueKind.Text,
                inputLabelKey,
                valueOptional,
                choices: null,
                validate: validate);

        /// <summary>Fixed-set choice override (e.g. forcing a Hoyoverse/BattleNet title).</summary>
        public static ProviderOverrideDescriptor Choice(
            string inputLabelKey,
            IReadOnlyList<ProviderOverrideChoice> choices,
            string invalidMessageKey)
        {
            var allowed = choices ?? Array.Empty<ProviderOverrideChoice>();
            return new ProviderOverrideDescriptor(
                ProviderOverrideValueKind.Choice,
                inputLabelKey,
                valueOptional: false,
                choices: allowed,
                validate: raw =>
                {
                    var trimmed = (raw ?? string.Empty).Trim();
                    var match = allowed.FirstOrDefault(choice =>
                        string.Equals(choice.Value, trimmed, StringComparison.OrdinalIgnoreCase));
                    return match != null
                        ? ProviderOverrideValidation.Valid(match.Value)
                        : ProviderOverrideValidation.Invalid(invalidMessageKey);
                });
        }
    }
}
