using System;
using System.IO;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TwinCatAdsTool.Gui.Themes
{
    /// <summary>
    /// Light/dark switch of the application and the one place that remembers the choice.
    /// The preference is kept next to the user profile rather than in the installation folder,
    /// which on a machine cabinet pc is usually not writable.
    /// </summary>
    public static class AppTheme
    {
        private const string DarkMarker = "dark";
        private const string LightMarker = "light";

        /// <summary>Dark is the default: this tool is used in front of a machine, not in an office.</summary>
        public static bool IsDark { get; private set; } = true;

        /// <summary>
        /// Applies the stored preference. Call it once the main window exists, otherwise the
        /// backdrop has no window to attach to.
        /// </summary>
        public static void ApplyStored()
        {
            Apply(ReadPreference());
        }

        public static void Apply(bool dark)
        {
            IsDark = dark;

            ApplicationThemeManager.Apply(
                dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
                WindowBackdropType.Mica,
                updateAccent: true);
        }

        /// <summary>Switches to the other theme and remembers it. Returns the new state.</summary>
        public static bool Toggle()
        {
            Apply(!IsDark);
            WritePreference(IsDark);
            return IsDark;
        }

        private static bool ReadPreference()
        {
            try
            {
                var file = PreferenceFile();
                if (!File.Exists(file))
                {
                    return true;
                }

                return !string.Equals(File.ReadAllText(file).Trim(), LightMarker,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // An unreadable preference is not worth failing the startup over.
                return true;
            }
        }

        private static void WritePreference(bool dark)
        {
            try
            {
                var file = PreferenceFile();
                Directory.CreateDirectory(Path.GetDirectoryName(file) ?? string.Empty);
                File.WriteAllText(file, dark ? DarkMarker : LightMarker);
            }
            catch (Exception)
            {
                // The theme still applies for this session, it just will not survive a restart.
            }
        }

        private static string PreferenceFile()
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TwinCatAdsTool",
                "theme.txt");
    }
}
