using Newtonsoft.Json.Linq;
using System.Linq;
using TwinCatAdsTool.Interfaces.Comparison;
using Xunit;

namespace TwinCatAdsTool.Logic.Tests
{
    /// <summary>
    /// Comparing two backups leaf by leaf rather than as text. The whole point is that the answer is
    /// about the plant and not about the file: key order and formatting must not show up as changes,
    /// and a variable present on one side only must not be passed over in silence.
    /// </summary>
    public class JsonDifferenceTests
    {
        private static JObject Json(string text) => JObject.Parse(text);

        [Fact]
        public void Two_identical_backups_differ_nowhere()
        {
            Assert.Empty(JsonDifference.Find(Json("{'a':1,'b':{'c':2}}"), Json("{'a':1,'b':{'c':2}}")));
        }

        [Fact]
        public void A_changed_leaf_is_reported_with_both_values()
        {
            var found = JsonDifference.Find(Json("{'a':1}"), Json("{'a':2}"));

            Assert.Single(found);
            Assert.Equal("a", found[0].Path);
            Assert.Equal("1", found[0].Left);
            Assert.Equal("2", found[0].Right);
        }

        [Fact]
        public void A_leaf_inside_a_structure_is_named_by_its_path()
        {
            var found = JsonDifference.Find(
                Json("{'MAIN':{'fb':{'Value':1}}}"),
                Json("{'MAIN':{'fb':{'Value':9}}}"));

            Assert.Equal("MAIN.fb.Value", found.Single().Path);
        }

        [Fact]
        public void An_array_element_is_named_by_its_position()
        {
            var found = JsonDifference.Find(Json("{'a':[1,2,3]}"), Json("{'a':[1,7,3]}"));

            Assert.Equal("a[1]", found.Single().Path);
        }

        [Fact]
        public void Key_order_is_not_a_difference()
        {
            Assert.Empty(JsonDifference.Find(Json("{'a':1,'b':2}"), Json("{'b':2,'a':1}")));
        }

        [Fact]
        public void Whitespace_is_not_a_difference()
        {
            Assert.Empty(JsonDifference.Find(Json("{'a':1}"), Json("{  'a' :   1  }")));
        }

        [Fact]
        public void A_variable_missing_from_the_right_is_reported()
        {
            var found = JsonDifference.Find(Json("{'a':1,'b':2}"), Json("{'a':1}")).Single();

            Assert.Equal("b", found.Path);
            Assert.Equal("2", found.Left);
            Assert.Null(found.Right);
            Assert.Contains("only on the left", found.ToString());
        }

        [Fact]
        public void A_variable_only_on_the_right_is_reported()
        {
            var found = JsonDifference.Find(Json("{'a':1}"), Json("{'a':1,'b':2}")).Single();

            Assert.Equal("b", found.Path);
            Assert.Null(found.Left);
            Assert.Contains("only on the right", found.ToString());
        }

        [Fact]
        public void An_array_that_grew_reports_the_new_positions()
        {
            var found = JsonDifference.Find(Json("{'a':[1]}"), Json("{'a':[1,2]}")).Single();

            Assert.Equal("a[1]", found.Path);
            Assert.Null(found.Left);
        }

        /// <summary>
        /// A value read back from the plc as 1.0 and one written as 1 are the same reading; json on
        /// its own cannot say which width the plc used.
        /// </summary>
        [Fact]
        public void A_whole_number_written_two_ways_is_one_value()
        {
            Assert.Empty(JsonDifference.Find(Json("{'a':1.0}"), Json("{'a':1}")));
        }

        [Fact]
        public void A_real_difference_survives_that()
        {
            Assert.Single(JsonDifference.Find(Json("{'a':1.5}"), Json("{'a':1.6}")));
        }

        [Fact]
        public void A_structure_replaced_by_a_value_is_a_difference()
        {
            var found = JsonDifference.Find(Json("{'a':{'b':1}}"), Json("{'a':5}"));

            Assert.Single(found);
        }

        [Fact]
        public void Booleans_and_strings_are_compared_as_they_read()
        {
            Assert.Empty(JsonDifference.Find(Json("{'a':true,'s':'x'}"), Json("{'a':true,'s':'x'}")));
            Assert.Single(JsonDifference.Find(Json("{'a':true}"), Json("{'a':false}")));
        }

        [Fact]
        public void Every_differing_leaf_is_listed_not_just_the_first()
        {
            var found = JsonDifference.Find(Json("{'a':1,'b':2,'c':3}"), Json("{'a':9,'b':2,'c':9}"));

            Assert.Equal(new[] { "a", "c" }, found.Select(entry => entry.Path));
        }

        /// <summary>
        /// The comparison window offers to show the whole backup and not only what differs, so the
        /// same walk has to be able to hand back every leaf with a mark against the ones that
        /// disagree. The command line keeps using Find, which allocates nothing for the agreements.
        /// </summary>
        [Fact]
        public void Comparing_lists_every_leaf_and_says_which_ones_differ()
        {
            var all = JsonDifference.Compare(Json("{'a':1,'b':2,'c':3}"), Json("{'a':9,'b':2,'c':3}"));

            Assert.Equal(new[] { "a", "b", "c" }, all.Select(entry => entry.Path));
            Assert.Equal(new[] { true, false, false }, all.Select(entry => entry.IsDifferent));
        }

        [Fact]
        public void A_leaf_present_on_one_side_only_is_named_after_the_side_that_has_it()
        {
            var onlyLeft = JsonDifference.Find(Json("{'a':1,'b':2}"), Json("{'a':1}")).Single();
            var onlyRight = JsonDifference.Find(Json("{'a':1}"), Json("{'a':1,'b':2}")).Single();

            Assert.Equal(JsonDifferenceKind.OnlyOnLeft, onlyLeft.Kind);
            Assert.Equal(JsonDifferenceKind.OnlyOnRight, onlyRight.Kind);
        }

        /// <summary>
        /// Only a leaf both sides hold can be carried across. Writing one that exists on a single
        /// side would mean declaring a symbol on the plc, which ads cannot do - so the window must
        /// not offer it rather than offer it and fail.
        /// </summary>
        [Fact]
        public void Only_a_leaf_both_sides_hold_can_be_carried_across()
        {
            Assert.True(JsonDifference.Find(Json("{'a':1}"), Json("{'a':2}")).Single().IsMergeable);
            Assert.False(JsonDifference.Find(Json("{'a':1,'b':2}"), Json("{'a':1}")).Single().IsMergeable);
            Assert.False(JsonDifference.Find(Json("{'a':1}"), Json("{'a':1,'b':2}")).Single().IsMergeable);
        }

        /// <summary>
        /// The path of an element is the position in the json array, which the restore then uses to
        /// address the element by position as well. It is not the index the plc declares: a backup
        /// does not record the lower bound of an array, so nothing here could reconstruct it.
        /// </summary>
        [Fact]
        public void An_element_is_named_by_its_position_in_the_array()
        {
            var found = JsonDifference.Find(Json("{'a':[1,2,3]}"), Json("{'a':[1,2,9]}")).Single();

            Assert.Equal("a[2]", found.Path);
        }
    }
}
