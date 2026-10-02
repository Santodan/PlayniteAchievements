using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Models
{
    /// <summary>
    /// Serializes a <see cref="TimeWindow"/> as its canonical string. Reads the legacy integer
    /// <see cref="TimelineRange"/> ordinal, the canonical string, an object with Preset/From/To,
    /// or null; anything unreadable yields the property's existing value so a hand-edited config
    /// never fails to load.
    /// </summary>
    public sealed class TimeWindowJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(TimeWindow);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is TimeWindow window)
            {
                writer.WriteValue(window.ToKey());
            }
            else
            {
                writer.WriteNull();
            }
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var fallback = existingValue as TimeWindow;
            switch (reader.TokenType)
            {
                case JsonToken.Null:
                case JsonToken.Undefined:
                    return fallback;
                case JsonToken.Integer:
                    return FromOrdinal(Convert.ToInt64(reader.Value), fallback);
                case JsonToken.String:
                    return TimeWindow.ParseOrDefault(reader.Value as string, fallback);
                case JsonToken.StartObject:
                    return FromObject(JObject.Load(reader), fallback);
                default:
                    reader.Skip();
                    return fallback;
            }
        }

        private static TimeWindow FromOrdinal(long ordinal, TimeWindow fallback)
        {
            if (ordinal < int.MinValue || ordinal > int.MaxValue)
            {
                return fallback;
            }

            var preset = (TimelineRange)(int)ordinal;
            return Enum.IsDefined(typeof(TimelineRange), preset) ? TimeWindow.FromPreset(preset) : fallback;
        }

        private static TimeWindow FromObject(JObject obj, TimeWindow fallback)
        {
            var presetToken = obj["Preset"];
            if (presetToken != null && presetToken.Type != JTokenType.Null)
            {
                if (presetToken.Type == JTokenType.Integer)
                {
                    return FromOrdinal(presetToken.Value<long>(), fallback);
                }

                return TimeWindow.ParseOrDefault(presetToken.ToString(), fallback);
            }

            if (!TryReadDay(obj["From"], out var from) || !TryReadDay(obj["To"], out var to))
            {
                return fallback;
            }

            return TimeWindow.Custom(from, to);
        }

        private static bool TryReadDay(JToken token, out DateTime? value)
        {
            value = null;
            if (token == null || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type == JTokenType.Date)
            {
                value = token.Value<DateTime>();
                return true;
            }

            if (token.Type == JTokenType.String &&
                DateTime.TryParse(
                    token.Value<string>(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var parsed))
            {
                value = parsed;
                return true;
            }

            return false;
        }
    }
}
