using System;
using TwinCAT.PlcOpen;
using TwinCatAdsTool.Logic.Values;
using Xunit;

namespace TwinCatAdsTool.Logic.Tests
{
    /// <summary>
    /// Json only knows long, double, bool and string. Without these conversions a restore would
    /// fail on every INT, BYTE or DT of the plc.
    /// </summary>
    public class ValueCoercionTests
    {
        [Fact]
        public void Narrows_a_json_integer_to_the_plc_type()
        {
            Assert.True(ValueCoercion.TryCoerce(7L, (short) 0, out var coerced));
            Assert.IsType<short>(coerced);
            Assert.Equal((short) 7, coerced);
        }

        [Theory]
        [InlineData((byte) 0)]
        [InlineData((sbyte) 0)]
        [InlineData((ushort) 0)]
        [InlineData((uint) 0)]
        [InlineData(0)]
        public void Narrows_to_every_integer_type_the_plc_uses(object template)
        {
            Assert.True(ValueCoercion.TryCoerce(5L, template, out var coerced));
            Assert.Equal(template.GetType(), coerced.GetType());
            Assert.Equal(5, Convert.ToInt32(coerced));
        }

        [Fact]
        public void Converts_a_json_double_to_a_plc_real()
        {
            Assert.True(ValueCoercion.TryCoerce(1.5d, 0f, out var coerced));
            Assert.IsType<float>(coerced);
            Assert.Equal(1.5f, coerced);
        }

        [Fact]
        public void Refuses_a_value_that_does_not_fit()
        {
            Assert.False(ValueCoercion.TryCoerce(70000L, (short) 0, out _));
        }

        [Fact]
        public void Refuses_text_where_a_number_is_expected()
        {
            Assert.False(ValueCoercion.TryCoerce("not a number", 0, out _));
        }

        [Fact]
        public void Keeps_a_value_that_already_has_the_right_type()
        {
            Assert.True(ValueCoercion.TryCoerce("text", "other", out var coerced));
            Assert.Equal("text", coerced);
        }

        [Fact]
        public void Converts_a_date_time_offset_back_into_a_plc_dt()
        {
            var moment = new DateTimeOffset(2026, 8, 24, 10, 30, 0, TimeSpan.Zero);

            Assert.True(ValueCoercion.TryCoerce(moment, new DT(), out var coerced));
            Assert.IsType<DT>(coerced);
            Assert.Equal(moment, InstantOf((DT) coerced));
        }

        /// <summary>
        /// DT holds a local wall clock time with no zone of its own, so the invariant that has to
        /// hold across a backup and restore is the instant itself.
        /// </summary>
        [Fact]
        public void Preserves_the_instant_of_a_utc_timestamp()
        {
            var moment = new DateTimeOffset(2026, 8, 24, 10, 30, 0, TimeSpan.Zero);

            Assert.True(ValueCoercion.TryCoerce(moment, new DT(), out var coerced));

            Assert.Equal(moment.UtcDateTime, InstantOf((DT) coerced).UtcDateTime);
        }

        /// <summary>
        /// A DateTime carrying an offset already applied must not have the local offset added
        /// a second time.
        /// </summary>
        [Fact]
        public void Does_not_shift_a_utc_kind_date_time()
        {
            var moment = new DateTime(2026, 8, 24, 10, 30, 0, DateTimeKind.Utc);

            Assert.True(ValueCoercion.TryCoerce(moment, new DT(), out var coerced));

            Assert.Equal(moment, InstantOf((DT) coerced).UtcDateTime);
        }

        [Fact]
        public void Converts_a_time_span_back_into_a_plc_time()
        {
            var span = TimeSpan.FromSeconds(90);

            Assert.True(ValueCoercion.TryCoerce(span, new TIME(), out var coerced));
            Assert.IsType<TIME>(coerced);
            Assert.Equal(span, ((TIME) coerced).Value);
        }

        [Fact]
        public void Converts_a_time_span_back_into_a_plc_ltime()
        {
            var span = TimeSpan.FromMilliseconds(1234);

            Assert.True(ValueCoercion.TryCoerce(span, new LTIME(), out var coerced));
            Assert.IsType<LTIME>(coerced);
            Assert.Equal(span, ((LTIME) coerced).Value);
        }

        /// <summary>
        /// Ads 7 hands the plc date types back as <see cref="DateTime"/>; version 5 used
        /// <see cref="DateTimeOffset"/>. The offset was an artefact of the wrapper - a plc DT has
        /// no time zone - so the backup now stores the timestamp as the plc holds it. What has to
        /// stay true is the round trip, which
        /// <see cref="Round_trips_a_plc_timestamp_through_json_and_back"/> covers.
        /// </summary>
        [Fact]
        public void Normalizes_plc_time_types_into_plain_values()
        {
            Assert.IsType<DateTime>(ValueCoercion.Normalize(new DT(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))));
            Assert.IsType<TimeSpan>(ValueCoercion.Normalize(new TIME(TimeSpan.FromSeconds(1))));
            Assert.IsType<TimeSpan>(ValueCoercion.Normalize(new LTIME(TimeSpan.FromSeconds(1))));
        }

        [Fact]
        public void Leaves_plain_values_untouched_when_normalizing()
        {
            Assert.Equal(42, ValueCoercion.Normalize(42));
            Assert.Null(ValueCoercion.Normalize(null));
        }

        [Fact]
        public void Round_trips_a_plc_timestamp_through_json_and_back()
        {
            var original = new DT(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

            var normalized = ValueCoercion.Normalize(original);
            Assert.True(ValueCoercion.TryCoerce(normalized, new DT(), out var restored));

            Assert.Equal(original.Ticks, ((DT) restored).Ticks);
            Assert.Equal(InstantOf(original), InstantOf((DT) restored));
        }

        /// <summary>
        /// Ads 7 exposes a DT as a <see cref="DateTime"/> with <see cref="DateTimeKind.Unspecified"/>
        /// holding local wall clock time - the plc type carries no zone. To reason about instants
        /// it has to be read back as local, which is how the library wrote it.
        /// </summary>
        private static DateTimeOffset InstantOf(DT dt) => new DateTimeOffset(dt.Value);

        private enum Mode
        {
            Idle = 0,
            Running = 1
        }

        [Fact]
        public void Converts_an_enum_from_its_number_and_from_its_name()
        {
            Assert.True(ValueCoercion.TryCoerce(1L, Mode.Idle, out var fromNumber));
            Assert.Equal(Mode.Running, fromNumber);

            Assert.True(ValueCoercion.TryCoerce("Running", Mode.Idle, out var fromName));
            Assert.Equal(Mode.Running, fromName);
        }

        [Fact]
        public void Refuses_null_where_the_plc_holds_a_value()
        {
            Assert.False(ValueCoercion.TryCoerce(null, 0, out _));
        }
    }
}
