using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace PlayniteAchievements.Providers.Settings
{
    /// <summary>
    /// Base class for provider-specific settings with common functionality.
    /// </summary>
    public abstract class ProviderSettingsBase : IProviderSettings
    {
        private bool _isEnabled = true;

        /// <inheritdoc />
        [JsonIgnore]
        public abstract string ProviderKey { get; }

        /// <inheritdoc />
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetValue(ref _isEnabled, value);
        }

        /// <inheritdoc />
        public virtual IProviderSettings Clone()
        {
            var json = SerializeToJson();
            var clone = (ProviderSettingsBase)Activator.CreateInstance(GetType());
            clone.DeserializeFromJson(json);
            return clone;
        }

        /// <inheritdoc />
        public virtual void CopyFrom(IProviderSettings source)
        {
            if (source == null) return;
            var json = source.SerializeToJson();
            DeserializeFromJson(json);
        }

        /// <summary>
        /// Serializes the settings to a JSON string, excluding ProviderKey.
        /// </summary>
        public virtual string SerializeToJson()
        {
            return JsonConvert.SerializeObject(this);
        }

        /// <summary>
        /// Populates settings from a JSON string.
        /// </summary>
        /// <param name="json">JSON string containing settings data.</param>
        public virtual void DeserializeFromJson(string json)
        {
            if (!string.IsNullOrEmpty(json))
            {
                JsonConvert.PopulateObject(json, this);
            }
        }

        /// <summary>
        /// Sets the property value and raises PropertyChanged if the value changed.
        /// </summary>
        protected bool SetValue<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// Raises the PropertyChanged event.
        /// </summary>
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <inheritdoc />
        public event PropertyChangedEventHandler PropertyChanged;
    }
}


