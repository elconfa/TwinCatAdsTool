using System;
using System.Globalization;
using TwinCAT.PlcOpen;

namespace TwinCatAdsTool.Logic.Values
{
    /// <summary>
    /// Adapts a value coming from json to the type the plc actually declares. Json only knows
    /// long, double, bool and string, so writing an INT or a DT straight from a parsed backup
    /// would fail - this is where that gets fixed.
    /// </summary>
    public static class ValueCoercion
    {
        /// <summary>
        /// Converts <paramref name="value"/> to the type of <paramref name="template"/>, which is
        /// the value currently held by the plc variable. Returns false when no sensible
        /// conversion exists, so the caller can report a mismatch instead of writing nonsense.
        /// </summary>
        public static bool TryCoerce(object value, object template, out object coerced)
        {
            coerced = null;

            if (template == null)
            {
                coerced = value;
                return true;
            }

            var targetType = template.GetType();

            if (value == null)
            {
                return false;
            }

            if (targetType.IsInstanceOfType(value))
            {
                coerced = value;
                return true;
            }

            try
            {
                if (targetType == typeof(DT))
                {
                    coerced = new DT(ToDateTimeOffset(value));
                    return true;
                }

                if (targetType == typeof(DATE))
                {
                    coerced = new DATE(ToDateTimeOffset(value));
                    return true;
                }

                if (targetType == typeof(TOD))
                {
                    coerced = new TOD(ToTimeSpan(value));
                    return true;
                }

                if (targetType == typeof(TIME))
                {
                    coerced = new TIME(ToTimeSpan(value));
                    return true;
                }

                if (targetType == typeof(LTIME))
                {
                    coerced = new LTIME(ToTimeSpan(value));
                    return true;
                }

                if (targetType == typeof(TimeSpan))
                {
                    // TimeSpan does not implement IConvertible, so the ChangeType below throws
                    // on it. This is the path every restore of a TIME, LTIME or TOD takes: those
                    // normalize to a TimeSpan, json has no notion of one, and a backup read back
                    // from disk therefore hands the value over as a string.
                    coerced = ToTimeSpan(value);
                    return true;
                }

                if (targetType.IsEnum)
                {
                    coerced = value is string text
                        ? Enum.Parse(targetType, text, ignoreCase: true)
                        : Enum.ToObject(targetType, Convert.ChangeType(value, Enum.GetUnderlyingType(targetType), CultureInfo.InvariantCulture));
                    return true;
                }

                coerced = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                // Overflow, format and cast problems all mean the same thing here: the backup
                // does not fit the variable any more.
                return false;
            }
        }

        /// <summary>
        /// Unwraps the PlcOpen wrapper types into plain .net values so they end up in the backup
        /// as readable iso timestamps rather than as nested objects.
        ///
        /// Ads 7 exposes these as <see cref="DateTime"/>; version 5 handed back a
        /// <see cref="DateTimeOffset"/> forced into the local time zone. A plc DT carries no time
        /// zone at all, so dropping the offset is what the type actually means - and it removes
        /// the shift a backup taken in one zone showed when restored in another.
        /// </summary>
        public static object Normalize(object value)
        {
            switch (value)
            {
                case DT dt:
                    return dt.Value;
                case DATE date:
                    return date.Value;
                case TOD tod:
                    return tod.Time;
                case TIME time:
                    return time.Value;
                case LTIME lTime:
                    return lTime.Value;
                default:
                    return value;
            }
        }

        /// <summary>
        /// The PlcOpen date types are built on DateTimeOffset and always hand the value back in
        /// the local time zone, so what has to survive a backup and restore is the instant, not
        /// its written form. A DateTime is therefore interpreted through its own Kind rather than
        /// being forced to either zone.
        /// </summary>
        private static DateTimeOffset ToDateTimeOffset(object value)
        {
            switch (value)
            {
                case DateTimeOffset dateTimeOffset:
                    return dateTimeOffset;
                case DateTime dateTime:
                    return dateTime.Kind == DateTimeKind.Utc
                        ? new DateTimeOffset(dateTime, TimeSpan.Zero)
                        : new DateTimeOffset(dateTime);
                default:
                    return DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture);
            }
        }

        private static TimeSpan ToTimeSpan(object value)
        {
            switch (value)
            {
                case TimeSpan timeSpan:
                    return timeSpan;
                case long ticks:
                    return TimeSpan.FromMilliseconds(ticks);
                case int milliseconds:
                    return TimeSpan.FromMilliseconds(milliseconds);
                default:
                    return TimeSpan.Parse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
        }
    }
}
