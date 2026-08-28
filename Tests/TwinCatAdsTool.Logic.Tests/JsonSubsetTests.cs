using Newtonsoft.Json.Linq;
using TwinCatAdsTool.Interfaces.Comparison;
using Xunit;

namespace TwinCatAdsTool.Logic.Tests
{
    /// <summary>
    /// Cutting a backup down to chosen leaves. What matters is not only that the wanted value comes
    /// across but that everything around it comes across as absence rather than as a value: the
    /// restore reads a null as "not asked for", so a subset that filled the gaps with anything else
    /// would write the whole variable instead of the one leaf that was picked.
    /// </summary>
    public class JsonSubsetTests
    {
        private static JObject Json(string text) => JObject.Parse(text.Replace('\'', '"'));

        [Fact]
        public void Nothing_asked_for_produces_nothing()
        {
            var pruned = JsonSubset.Prune(Json("{'a':1}"), new string[0]);

            Assert.Empty(pruned.Properties());
        }

        [Fact]
        public void A_top_level_value_is_carried_over_on_its_own()
        {
            var pruned = JsonSubset.Prune(Json("{'a':1,'b':2}"), new[] {"a"});

            Assert.Equal(1, pruned["a"]);
            Assert.Null(pruned["b"]);
        }

        [Fact]
        public void A_nested_value_brings_the_objects_above_it_but_not_its_siblings()
        {
            var pruned = JsonSubset.Prune(Json("{'g':{'x':1,'y':2},'h':3}"), new[] {"g.x"});

            Assert.Equal(1, pruned["g"]["x"]);
            Assert.Null(pruned["g"]["y"]);
            Assert.Null(pruned["h"]);
        }

        [Fact]
        public void Two_values_under_the_same_parent_share_it()
        {
            var pruned = JsonSubset.Prune(Json("{'g':{'x':1,'y':2,'z':3}}"), new[] {"g.x", "g.z"});

            Assert.Equal(1, pruned["g"]["x"]);
            Assert.Equal(3, pruned["g"]["z"]);
            Assert.Null(pruned["g"]["y"]);
        }

        /// <summary>
        /// The array keeps its length. The restore addresses elements by position, so an array cut
        /// down to the one element that was picked would write that value into the first element of
        /// the plc array instead of the third.
        /// </summary>
        [Fact]
        public void An_element_keeps_its_position_in_an_array_of_the_original_length()
        {
            var pruned = JsonSubset.Prune(Json("{'a':[10,20,30,40]}"), new[] {"a[2]"});

            var array = (JArray) pruned["a"];
            Assert.Equal(4, array.Count);
            Assert.Equal(JTokenType.Null, array[0].Type);
            Assert.Equal(JTokenType.Null, array[1].Type);
            Assert.Equal(30, array[2]);
            Assert.Equal(JTokenType.Null, array[3].Type);
        }

        [Fact]
        public void A_value_inside_an_element_of_an_array_is_reached_through_it()
        {
            var pruned = JsonSubset.Prune(
                Json("{'m':{'axes':[{'p':1,'q':2},{'p':3,'q':4}]}}"),
                new[] {"m.axes[1].p"});

            var axes = (JArray) pruned["m"]["axes"];
            Assert.Equal(2, axes.Count);
            Assert.Equal(JTokenType.Null, axes[0].Type);
            Assert.Equal(3, axes[1]["p"]);
            Assert.Null(axes[1]["q"]);
        }

        [Fact]
        public void An_array_of_arrays_keeps_both_lengths()
        {
            var pruned = JsonSubset.Prune(Json("{'a':[[1,2,3],[4,5,6]]}"), new[] {"a[1][2]"});

            var outer = (JArray) pruned["a"];
            Assert.Equal(2, outer.Count);
            Assert.Equal(JTokenType.Null, outer[0].Type);

            var inner = (JArray) outer[1];
            Assert.Equal(3, inner.Count);
            Assert.Equal(6, inner[2]);
        }

        [Fact]
        public void A_whole_structure_can_be_taken_at_once()
        {
            var pruned = JsonSubset.Prune(Json("{'g':{'x':1,'y':2},'h':3}"), new[] {"g"});

            Assert.Equal(1, pruned["g"]["x"]);
            Assert.Equal(2, pruned["g"]["y"]);
            Assert.Null(pruned["h"]);
        }

        /// <summary>The subset must not share nodes with the backup it was cut from.</summary>
        [Fact]
        public void The_value_is_copied_rather_than_shared()
        {
            var source = Json("{'g':{'x':1}}");
            var pruned = JsonSubset.Prune(source, new[] {"g"});

            pruned["g"]["x"] = 99;

            Assert.Equal(1, source["g"]["x"]);
        }

        [Fact]
        public void A_path_the_backup_does_not_hold_is_left_out_rather_than_invented()
        {
            var pruned = JsonSubset.Prune(Json("{'a':1}"), new[] {"b", "a.deeper", "c[0]"});

            Assert.Empty(pruned.Properties());
        }

        [Fact]
        public void An_element_past_the_end_of_the_array_is_left_out()
        {
            var pruned = JsonSubset.Prune(Json("{'a':[1,2]}"), new[] {"a[5]"});

            Assert.Empty(pruned.Properties());
        }

        [Theory]
        [InlineData("")]
        [InlineData(".a")]
        [InlineData("a.")]
        [InlineData("a..b")]
        [InlineData("[0]")]
        [InlineData("a.[0]")]
        [InlineData("a[x]")]
        [InlineData("a[0")]
        public void A_path_that_is_not_a_path_is_refused_rather_than_half_read(string path)
        {
            var pruned = JsonSubset.Prune(Json("{'a':[{'b':1}]}"), new[] {path});

            Assert.Empty(pruned.Properties());
        }

        /// <summary>
        /// The plc spells a name one way and a backup written by an older version of this tool may
        /// spell it another; the rest of the restore matches names without regard to case, and this
        /// has to agree with it.
        /// </summary>
        [Fact]
        public void Names_are_matched_without_regard_to_case()
        {
            var pruned = JsonSubset.Prune(Json("{'GVL':{'Counter':7}}"), new[] {"gvl.counter"});

            Assert.Equal(7, pruned["gvl"]["counter"]);
        }
    }
}
