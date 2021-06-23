using System.IO;
using System.IO.Compression;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
    /// <summary>
    /// Generating the minimap takes a lot of time at startup and is generated from WorldGenerator. Therefore the
    /// minimap never changes in a given world.
    /// Now it is being saved inside the world folder as a zipped file with the name worldName_worldSeed_gameVersion.map
    /// </summary>
    [HarmonyPatch(typeof(Minimap), "GenerateWorldMap")]
    public static class MinimapPatches
    {
        private static bool Prefix(Minimap __instance)
        {
            // try to load existing textures
            if (File.Exists(FilePath()))
            {
                LoadFromFile(__instance);
                return false;
            }

            // compute textures normally
            return true;
        }

        private static void Postfix(Minimap __instance)
        {
            if (!File.Exists(FilePath()))
            {
                // write computed textures to file
                Directory.CreateDirectory(MinimapSaveFolderPath());
                SaveToFile(__instance);
            }
        }

        private static void SaveToFile(Minimap minimap)
        {
            using (FileStream fileStream = File.Create(FilePath()))
            using (GZipStream compressionStream = new GZipStream(fileStream, CompressionMode.Compress))
            {
                ZPackage package = new ZPackage();
                package.Write(minimap.m_forestMaskTexture.GetRawTextureData());
                package.Write(minimap.m_mapTexture.GetRawTextureData());
                package.Write(minimap.m_heightTexture.GetRawTextureData());

                byte[] data = package.GetArray();
                compressionStream.Write(data, 0, data.Length);
            }
        }

        private static void LoadFromFile(Minimap minimap)
        {
            using (FileStream fileStream = File.OpenRead(FilePath()))
            using (GZipStream decompressionStream = new GZipStream(fileStream, CompressionMode.Decompress))
            using (MemoryStream resultStream = new MemoryStream())
            {
                decompressionStream.CopyTo(resultStream);
                ZPackage package = new ZPackage(resultStream.ToArray());

                minimap.m_forestMaskTexture.LoadRawTextureData(package.ReadByteArray());
                minimap.m_forestMaskTexture.Apply();
                minimap.m_mapTexture.LoadRawTextureData(package.ReadByteArray());
                minimap.m_mapTexture.Apply();
                minimap.m_heightTexture.LoadRawTextureData(package.ReadByteArray());
                minimap.m_heightTexture.Apply();
            }
        }

        public static string FilePath()
        {
            string file = ZNet.m_world.m_name + "_" + ZNet.m_world.m_seed + "_" + Version.GetVersionString() + ".map";
            return MinimapSaveFolderPath() + "/" + file;
        }

        public static string MinimapSaveFolderPath()
        {
            return World.GetWorldSavePath() + "/minimap";
        }
    }
}
