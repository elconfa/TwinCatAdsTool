using TwinCatAdsTool.Logic.Cli;
using Xunit;

namespace TwinCatAdsTool.Logic.Tests
{
    /// <summary>
    /// The command line is read in full before anything happens, so that a line that cannot work is
    /// refused before a connection is opened or a file is touched. What is checked here is mostly
    /// that bad input is caught early and named precisely.
    /// </summary>
    public class CommandLineTests
    {
        private static CliCommand Parse(params string[] args) => CommandLine.Parse(args);

        [Fact]
        public void No_arguments_asks_for_the_window()
        {
            Assert.Equal(CliVerb.None, Parse().Verb);
            Assert.True(Parse().IsValid);
        }

        [Fact]
        public void A_backup_carries_target_and_file()
        {
            var command = Parse("backup", "5.24.108.31.1.1", "851", @"C:\plant.json");

            Assert.True(command.IsValid);
            Assert.Equal(CliVerb.Backup, command.Verb);
            Assert.Equal("5.24.108.31.1.1", command.AmsNetId);
            Assert.Equal(851, command.Port);
            Assert.Equal(@"C:\plant.json", command.File);
        }

        [Theory]
        [InlineData("restore", CliVerb.Restore)]
        [InlineData("compare", CliVerb.Compare)]
        [InlineData("BACKUP", CliVerb.Backup)]
        [InlineData("--restore", CliVerb.Restore)]
        public void Verbs_are_read_whatever_the_spelling(string verb, CliVerb expected)
        {
            Assert.Equal(expected, Parse(verb, "1.2.3.4.5.6", "851", "f.json").Verb);
        }

        /// <summary>
        /// Anyone coming from the beckhoff symbol explorer has these three in their scripts already.
        /// </summary>
        [Theory]
        [InlineData("--SnapShotFromPlc", CliVerb.Backup)]
        [InlineData("--SyncPlcToSnapShot", CliVerb.Backup)]
        [InlineData("--SyncSnapShotToPlc", CliVerb.Restore)]
        public void The_symbol_explorer_spellings_are_understood_too(string verb, CliVerb expected)
        {
            Assert.Equal(expected, Parse(verb, "1.2.3.4.5.6", "851", "f.json").Verb);
        }

        [Theory]
        [InlineData("--help")]
        [InlineData("-h")]
        [InlineData("help")]
        public void Help_is_asked_for_in_the_usual_ways(string argument)
        {
            Assert.Equal(CliVerb.Help, Parse(argument).Verb);
        }

        [Fact]
        public void Version_is_a_verb_of_its_own()
        {
            Assert.Equal(CliVerb.Version, Parse("--version").Verb);
        }

        [Fact]
        public void An_unknown_command_is_named_in_the_complaint()
        {
            var command = Parse("upload", "1.2.3.4.5.6", "851", "f.json");

            Assert.False(command.IsValid);
            Assert.Contains("upload", command.Error);
        }

        [Fact]
        public void Missing_arguments_are_counted()
        {
            var command = Parse("backup", "1.2.3.4.5.6", "851");

            Assert.False(command.IsValid);
            Assert.Contains("three arguments", command.Error);
        }

        [Fact]
        public void Too_many_arguments_are_refused()
        {
            Assert.False(Parse("backup", "1.2.3.4.5.6", "851", "f.json", "extra").IsValid);
        }

        /// <summary>
        /// A mistyped net id would otherwise come back much later as a timeout, which reads like a
        /// network problem rather than a typing one.
        /// </summary>
        [Theory]
        [InlineData("192.168.0.1")]
        [InlineData("5.24.108.31.1")]
        [InlineData("5.24.108.31.1.1.1")]
        [InlineData("5.24.108.999.1.1")]
        [InlineData("not.an.id.at.all.here")]
        public void Something_that_is_not_an_ams_net_id_is_refused(string candidate)
        {
            var command = Parse("backup", candidate, "851", "f.json");

            Assert.False(command.IsValid);
            Assert.Contains("ams net id", command.Error);
        }

        [Fact]
        public void An_ams_net_id_may_carry_a_zero_part()
        {
            Assert.True(Parse("backup", "5.0.108.31.1.1", "851", "f.json").IsValid);
        }

        [Theory]
        [InlineData("851x")]
        [InlineData("0")]
        [InlineData("65536")]
        [InlineData("-1")]
        public void Something_that_is_not_a_port_is_refused(string candidate)
        {
            var command = Parse("backup", "1.2.3.4.5.6", candidate, "f.json");

            Assert.False(command.IsValid);
            Assert.Contains("port", command.Error);
        }

        [Fact]
        public void An_empty_file_argument_is_refused()
        {
            Assert.False(Parse("backup", "1.2.3.4.5.6", "851", "   ").IsValid);
        }
    }
}
