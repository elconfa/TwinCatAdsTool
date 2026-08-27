using TwinCatAdsTool.Interfaces.Scope;
using Xunit;

namespace TwinCatAdsTool.Logic.Tests
{
    /// <summary>
    /// A trigger exists to catch the moment something changed, so what matters is that it answers to
    /// a crossing and not to a state: a condition that fires on a signal which was already in the
    /// wanted state would fire the instant it is armed, every time, and catch nothing.
    /// </summary>
    public class TriggerConditionTests
    {
        private static TriggerCondition Bit(TriggerEdge edge) => new TriggerCondition(edge, 0);

        private static TriggerCondition Level(TriggerEdge edge, double level) => new TriggerCondition(edge, level);

        [Fact]
        public void The_first_reading_never_fires()
        {
            Assert.False(Bit(TriggerEdge.GoesTrue).Fires(null, 1));
            Assert.False(Bit(TriggerEdge.GoesFalse).Fires(null, 0));
            Assert.False(Level(TriggerEdge.RisesAbove, 10).Fires(null, 99));
            Assert.False(Level(TriggerEdge.FallsBelow, 10).Fires(null, 1));
        }

        [Fact]
        public void A_bit_going_true_fires_on_the_edge()
        {
            Assert.True(Bit(TriggerEdge.GoesTrue).Fires(0, 1));
        }

        [Fact]
        public void A_bit_already_true_does_not_fire()
        {
            Assert.False(Bit(TriggerEdge.GoesTrue).Fires(1, 1));
        }

        [Fact]
        public void A_bit_going_false_fires_on_the_edge()
        {
            Assert.True(Bit(TriggerEdge.GoesFalse).Fires(1, 0));
            Assert.False(Bit(TriggerEdge.GoesFalse).Fires(0, 0));
        }

        [Fact]
        public void A_value_crossing_upwards_fires_once()
        {
            var condition = Level(TriggerEdge.RisesAbove, 100);

            Assert.True(condition.Fires(99, 101));
            Assert.False(condition.Fires(101, 102));
        }

        [Fact]
        public void A_value_reaching_the_level_exactly_counts_as_crossing_it()
        {
            Assert.True(Level(TriggerEdge.RisesAbove, 100).Fires(99, 100));
            Assert.True(Level(TriggerEdge.FallsBelow, 100).Fires(101, 100));
        }

        [Fact]
        public void A_value_crossing_downwards_fires_once()
        {
            var condition = Level(TriggerEdge.FallsBelow, 100);

            Assert.True(condition.Fires(101, 99));
            Assert.False(condition.Fires(99, 98));
        }

        [Fact]
        public void A_value_moving_the_wrong_way_does_not_fire()
        {
            Assert.False(Level(TriggerEdge.RisesAbove, 100).Fires(101, 99));
            Assert.False(Level(TriggerEdge.FallsBelow, 100).Fires(99, 101));
        }

        [Fact]
        public void A_value_that_does_not_reach_the_level_does_not_fire()
        {
            Assert.False(Level(TriggerEdge.RisesAbove, 100).Fires(10, 99));
        }

        [Fact]
        public void A_negative_level_is_crossed_like_any_other()
        {
            Assert.True(Level(TriggerEdge.FallsBelow, -5).Fires(-4, -6));
            Assert.False(Level(TriggerEdge.FallsBelow, -5).Fires(-6, -7));
        }
    }
}
