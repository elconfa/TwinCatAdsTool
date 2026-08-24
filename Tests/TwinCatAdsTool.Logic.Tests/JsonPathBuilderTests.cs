using System;
using Newtonsoft.Json.Linq;
using TwinCatAdsTool.Logic.Services;
using Xunit;

namespace TwinCatAdsTool.Logic.Tests
{
    public class JsonPathBuilderTests
    {
        [Fact]
        public void Places_a_two_segment_path()
        {
            var root = new JObject();

            JsonPathBuilder.Insert(root, "GVL.Counter", new JValue(42));

            Assert.Equal(42, root["GVL"]["Counter"].Value<int>());
        }

        [Fact]
        public void Places_a_deeply_nested_path()
        {
            var root = new JObject();

            JsonPathBuilder.Insert(root, "MAIN.Machine.Station.Temperature", new JValue(21.5));

            Assert.Equal(21.5, root["MAIN"]["Machine"]["Station"]["Temperature"].Value<double>());
        }

        /// <summary>
        /// Regression test for the defect that made variables land in the wrong node: the old
        /// code derived the parent path with Replace("." + localName, ""), which strips every
        /// occurrence of the name rather than the last segment only.
        /// </summary>
        [Fact]
        public void Keeps_repeated_names_apart()
        {
            var root = new JObject();

            JsonPathBuilder.Insert(root, "GVL.Axis.Axis", new JValue(7));

            // The inner Axis is nested inside the outer one, not collapsed onto GVL.
            Assert.IsType<JObject>(root["GVL"]["Axis"]);
            Assert.Equal(7, root["GVL"]["Axis"]["Axis"].Value<int>());
            Assert.Single((JObject) root["GVL"]);
        }

        [Fact]
        public void Demonstrates_the_old_replace_defect()
        {
            const string instancePath = "GVL.Axis.Axis";
            var localName = instancePath.Split('.')[2];

            var oldParent = instancePath.Replace($".{localName}", string.Empty);

            // The old code believed the parent of GVL.Axis.Axis was GVL, not GVL.Axis.
            Assert.Equal("GVL", oldParent);
            Assert.NotEqual("GVL.Axis", oldParent);
        }

        [Fact]
        public void Merges_siblings_under_a_common_parent()
        {
            var root = new JObject();

            JsonPathBuilder.Insert(root, "GVL.Data.A", new JValue(1));
            JsonPathBuilder.Insert(root, "GVL.Data.B", new JValue(2));

            Assert.Equal(1, root["GVL"]["Data"]["A"].Value<int>());
            Assert.Equal(2, root["GVL"]["Data"]["B"].Value<int>());
        }

        [Fact]
        public void Refuses_to_overwrite_an_existing_value()
        {
            var root = new JObject();
            JsonPathBuilder.Insert(root, "GVL.Value", new JValue(1));

            Assert.Throws<InvalidOperationException>(() => JsonPathBuilder.Insert(root, "GVL.Value", new JValue(2)));
        }

        [Fact]
        public void Refuses_a_path_that_runs_through_a_leaf()
        {
            var root = new JObject();
            JsonPathBuilder.Insert(root, "GVL.Value", new JValue(1));

            Assert.Throws<InvalidOperationException>(() => JsonPathBuilder.Insert(root, "GVL.Value.Inner", new JValue(2)));
        }

        [Fact]
        public void Finds_what_it_inserted()
        {
            var root = new JObject();
            JsonPathBuilder.Insert(root, "GVL.Axis.Axis", new JValue(7));

            Assert.Equal(7, JsonPathBuilder.Find(root, "GVL.Axis.Axis").Value<int>());
            Assert.Null(JsonPathBuilder.Find(root, "GVL.Missing"));
            Assert.Null(JsonPathBuilder.Find(root, "GVL.Axis.Axis.TooDeep"));
        }

        [Fact]
        public void Find_ignores_case_like_the_plc_does()
        {
            var root = new JObject();
            JsonPathBuilder.Insert(root, "GVL.Counter", new JValue(3));

            Assert.Equal(3, JsonPathBuilder.Find(root, "gvl.counter").Value<int>());
        }
    }
}
