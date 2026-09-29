using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Represents object implementing INotifyPropertyChanged.
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        /// <summary>
        /// If set to <c>true</c> no <see cref="PropertyChanged"/> events will be fired.
        /// </summary>
        internal bool SuppressNotifications
        {
            get; set;
        } = false;

        /// <summary>
        /// Occurs when a property value changes
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Invokes PropertyChanged events.
        /// </summary>
        /// <param name="name">Name of property that changed.</param>
        public void OnPropertyChanged([CallerMemberName] string name = null)
        {
            if (!SuppressNotifications)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        /// <summary>
        /// Called just before a value is replaced, with the value it is replacing.
        /// </summary>
        /// <remarks>
        /// The setter is the only place the old value still exists, so a derived type that needs
        /// to remember what a value was - an undo history - observes it here. Does nothing by
        /// default, so nothing changes for the types that do not.
        /// </remarks>
        protected virtual void OnValueChanging(string propertyName, object oldValue, object newValue)
        {
        }

        protected void SetValue<T>(ref T property, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(property, value))
            {
                return;
            }

            OnValueChanging(propertyName, property, value);
            property = value;
            OnPropertyChanged(propertyName);
        }

        protected bool SetValueAndReturn<T>(ref T property, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(property, value))
            {
                return false;
            }

            OnValueChanging(propertyName, property, value);
            property = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void SetValue<T>(ref T property, T value, params string[] propertyNames)
        {
            property = value;
            foreach (var pro in propertyNames)
            {
                OnPropertyChanged(pro);
            }
        }
    }
}
