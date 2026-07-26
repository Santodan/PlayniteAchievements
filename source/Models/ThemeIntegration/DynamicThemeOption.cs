using Playnite.SDK.Data;
using System;
using System.Runtime.Serialization;
using System.Windows.Input;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    /// <summary>
    /// Key/label pair used by fullscreen themes for dynamic list ComboBox bindings.
    /// </summary>
    public sealed class DynamicThemeOption : System.IEquatable<DynamicThemeOption>
    {
        public DynamicThemeOption(
            string key,
            string label,
            int count = 0,
            bool isSelected = false,
            ICommand applyCommand = null,
            object commandParameter = null)
        {
            Key = key ?? string.Empty;
            Label = label ?? Key;
            Count = count < 0 ? 0 : count;
            IsSelected = isSelected;
            CommandParameter = commandParameter;
            ApplyCommand = applyCommand == null
                ? null
                : new OptionApplyCommand(applyCommand, this);
        }

        public string Key { get; }

        public string Label { get; }

        public int Count { get; }

        public bool IsSelected { get; }

        [DontSerialize]
        [IgnoreDataMember]
        public ICommand ApplyCommand { get; }

        [DontSerialize]
        [IgnoreDataMember]
        public object CommandParameter { get; }

        public bool Equals(DynamicThemeOption other)
        {
            if (other == null)
            {
                return false;
            }

            return string.Equals(Key, other.Key, System.StringComparison.Ordinal) &&
                   string.Equals(Label, other.Label, System.StringComparison.Ordinal) &&
                   Count == other.Count &&
                   IsSelected == other.IsSelected;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as DynamicThemeOption);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = (hash * 31) + System.StringComparer.Ordinal.GetHashCode(Key ?? string.Empty);
                hash = (hash * 31) + System.StringComparer.Ordinal.GetHashCode(Label ?? string.Empty);
                hash = (hash * 31) + Count.GetHashCode();
                hash = (hash * 31) + IsSelected.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
        {
            return Label;
        }

        private sealed class OptionApplyCommand : ICommand
        {
            private readonly ICommand _innerCommand;
            private readonly DynamicThemeOption _owner;

            public OptionApplyCommand(ICommand innerCommand, DynamicThemeOption owner)
            {
                _innerCommand = innerCommand ?? throw new ArgumentNullException(nameof(innerCommand));
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }

            public bool CanExecute(object parameter)
            {
                var resolvedParameter = ResolveParameter(parameter);
                return _innerCommand.CanExecute(resolvedParameter);
            }

            public void Execute(object parameter)
            {
                var resolvedParameter = ResolveParameter(parameter);
                if (_innerCommand.CanExecute(resolvedParameter))
                {
                    _innerCommand.Execute(resolvedParameter);
                }
            }

            public event EventHandler CanExecuteChanged
            {
                add => _innerCommand.CanExecuteChanged += value;
                remove => _innerCommand.CanExecuteChanged -= value;
            }

            private object ResolveParameter(object parameter)
            {
                return parameter ?? _owner.CommandParameter ?? _owner;
            }
        }
    }
}
