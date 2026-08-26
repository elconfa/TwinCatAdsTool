using System;
using System.Reflection;

namespace TwinCatAdsTool.Interfaces
{
	public static class Constants
	{
		public const string LoggingRepositoryName = "TwinCatAdsTool";
		public const string LoggingObservationRepositoryName = "observation";

		/// <summary>
		/// Shown in the window and written into every diagnostic file. Read from the assembly
		/// rather than written here: the release is built from a tag which sets the version of
		/// every assembly, so the tag, the binary and what the user reads on screen are one and
		/// the same. Written by hand, they drift - and a bug report then names a version that
		/// never existed.
		/// </summary>
		public static readonly string Version = ReadVersion();

		private static string ReadVersion()
		{
			var informational = typeof(Constants).Assembly
				.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

			if (string.IsNullOrEmpty(informational))
			{
				return typeof(Constants).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
			}

			// The sdk appends the source revision as +<commit sha>, which is not worth showing.
			var revision = informational.IndexOf('+');
			return revision < 0 ? informational : informational.Substring(0, revision);
		}
	}
}
