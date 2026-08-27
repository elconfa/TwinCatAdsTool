using System;
using System.Globalization;
using System.Linq;

namespace TwinCatAdsTool.Logic.Cli
{
    public enum CliVerb
    {
        None,
        Backup,
        Restore,
        Compare,
        Help,
        Version
    }

    /// <summary>
    /// What the command line asked for, or why it could not be understood. Nothing is acted on until
    /// the whole line has been read: a restore that has already connected to a plc before noticing
    /// that the file argument is missing has done work nobody asked for.
    /// </summary>
    public class CliCommand
    {
        private CliCommand(CliVerb verb, string amsNetId, int port, string file, string error)
        {
            Verb = verb;
            AmsNetId = amsNetId;
            Port = port;
            File = file;
            Error = error;
        }

        public CliVerb Verb { get; }

        public string AmsNetId { get; }

        public int Port { get; }

        public string File { get; }

        /// <summary>Why the line could not be understood, or null when it could.</summary>
        public string Error { get; }

        public bool IsValid => Error == null;

        internal static CliCommand Of(CliVerb verb, string amsNetId, int port, string file)
            => new CliCommand(verb, amsNetId, port, file, null);

        internal static CliCommand Invalid(string error) => new CliCommand(CliVerb.None, null, 0, null, error);
    }

    public static class CommandLine
    {
        public const string Usage = @"TwinCatAdsTool - backup and restore of persistent plc variables

  TwinCatAdsTool                                   open the window
  TwinCatAdsTool backup  <netid> <port> <file>     read every persistent variable into a json file
  TwinCatAdsTool restore <netid> <port> <file>     write a json file back onto the plc
  TwinCatAdsTool compare <netid> <port> <file>     read the plc and report how it differs from a file
  TwinCatAdsTool --help
  TwinCatAdsTool --version

  <netid>   ams net id of the target, for instance 5.24.108.31.1.1
  <port>    ams port of the plc runtime, usually 851
  <file>    path to the json file

Exit codes
  0   done, and everything was processed
  1   the command line could not be understood
  2   the plc could not be reached
  3   the run finished but some variables failed or were skipped
  4   something unexpected went wrong
  5   compare only: the plc and the file differ

A restore writes to the plc without asking. That is the point of a command line, and it is worth
saying out loud.";

        public static CliCommand Parse(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return CliCommand.Of(CliVerb.None, null, 0, null);
            }

            var first = args[0];

            if (Matches(first, "--help", "-h", "-?", "/?", "help"))
            {
                return CliCommand.Of(CliVerb.Help, null, 0, null);
            }

            if (Matches(first, "--version", "-v", "version"))
            {
                return CliCommand.Of(CliVerb.Version, null, 0, null);
            }

            var verb = ReadVerb(first);

            if (verb == CliVerb.None)
            {
                return CliCommand.Invalid($"'{first}' is not a command. Try --help.");
            }

            if (args.Length != 4)
            {
                return CliCommand.Invalid(
                    $"{first} needs three arguments - net id, port and file - but was given {args.Length - 1}.");
            }

            if (!IsAmsNetId(args[1]))
            {
                return CliCommand.Invalid(
                    $"'{args[1]}' is not an ams net id. It has six parts, for instance 5.24.108.31.1.1.");
            }

            if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
                port < 1 || port > 65535)
            {
                return CliCommand.Invalid($"'{args[2]}' is not a port. The plc runtime is usually on 851.");
            }

            if (string.IsNullOrWhiteSpace(args[3]))
            {
                return CliCommand.Invalid("The file argument is empty.");
            }

            return CliCommand.Of(verb, args[1], port, args[3]);
        }

        private static CliVerb ReadVerb(string argument)
        {
            // The double dashed spellings are the ones the beckhoff symbol explorer uses for the same
            // three operations; accepting them costs nothing and saves reading two manuals.
            if (Matches(argument, "backup", "--backup", "--SnapShotFromPlc", "--SyncPlcToSnapShot"))
            {
                return CliVerb.Backup;
            }

            if (Matches(argument, "restore", "--restore", "--SyncSnapShotToPlc"))
            {
                return CliVerb.Restore;
            }

            return Matches(argument, "compare", "--compare") ? CliVerb.Compare : CliVerb.None;
        }

        private static bool Matches(string argument, params string[] accepted)
            => accepted.Any(candidate => string.Equals(argument, candidate, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Six numbers separated by dots, each of them a byte. Checked here rather than left to ads,
        /// because a mistyped net id otherwise surfaces much later as a timeout, which reads like a
        /// network problem instead of a typing one.
        /// </summary>
        private static bool IsAmsNetId(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            var parts = candidate.Split('.');

            return parts.Length == 6 && parts.All(part =>
                byte.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
        }
    }
}
