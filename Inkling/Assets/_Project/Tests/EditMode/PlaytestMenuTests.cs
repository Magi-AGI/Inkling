using System.IO;
using NUnit.Framework;

namespace Magi.Inkling.Tests.EditMode
{
    /// <summary>
    /// CP7a: the dev thermal playtest configs must live under the generalized `Inkling/Playtests/`
    /// submenu (so future playtests can nest the same way), not the old flat root. Source-level
    /// assertion — no scene/runtime dependency. The stale path is built by concatenation so this
    /// test source never contains the exact deprecated literal (keeps repo greps clean).
    /// </summary>
    public class PlaytestMenuTests
    {
        private const string ConfiguratorPath =
            "Assets/_Project/Scripts/Dev/Editor/ThermalPlaytestConfigurator.cs";

        // Built at runtime so the exact deprecated menu path does not appear in this file's source.
        private static readonly string NewMenuRoot = "Inkling/" + "Playtests/Thermal/";
        private static readonly string OldFlatMenuRoot = "Inkling/" + "Thermal Playtest/";

        [Test]
        public void ThermalPlaytest_MenuIsUnderPlaytestsRoot()
        {
            Assert.IsTrue(File.Exists(ConfiguratorPath), $"Configurator not found at {ConfiguratorPath}");
            string src = File.ReadAllText(ConfiguratorPath);

            StringAssert.Contains(NewMenuRoot, src,
                $"Thermal playtest menu should live under {NewMenuRoot}");
            Assert.IsFalse(src.Contains(OldFlatMenuRoot),
                $"Stale menu/log path '{OldFlatMenuRoot}' must be fully removed (menu items and log text)");
        }
    }
}
