using Jularr.Backend.Logic.Library;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class GameFileNameTests
    {
        [TestMethod]
        [DataRow("Roms/Super Game (USA, Europe) (Rev 1).sfc", "USA, Europe", "Rev 1")]
        [DataRow("Super Game (Japan).sfc", "Japan", null)]
        [DataRow("Roms\\Super Game (World) (v1.1).gba", "World", "v1.1")]
        [DataRow("Zelda [0100ABC000000000][v65536].nsp", null, "v65536")]
        [DataRow("Some Game v2.0.1 GOG.exe", null, "v2.0.1")]
        [DataRow("Some Game (Disc 1).iso", null, null)]
        [DataRow("Plain.rom", null, null)]
        public void Facts_ReadRegionAndVersionFromTheName(string path, string? region, string? version)
        {
            var facts = GameFileName.Facts(path);
            Assert.AreEqual(region, facts.Region);
            Assert.AreEqual(version, facts.Version);
        }
    }
}
