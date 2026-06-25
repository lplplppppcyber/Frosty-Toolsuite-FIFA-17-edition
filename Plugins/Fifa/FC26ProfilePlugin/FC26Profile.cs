using FrostySdk;
using FrostySdk.BaseProfile;
using FrostySdk.Interfaces;
using System;
using System.Collections.Generic;

namespace FC26ProfilePlugin
{
    public class FC26Profile : IProfile
    {
        public Type BinarySbReaderType => typeof(BaseBinarySbReader);
        public Type CompressionUtilsType => typeof(BaseCompressionUtils);

        public IBinarySbReader GetBinarySbReader() => new BaseBinarySbReader();
        public ICompressionUtils GetCompressionUtils() => new BaseCompressionUtils();

        public Profile CreateProfile()
        {
            return new Profile
            {
                Name = "FC26",
                DisplayName = "EA SPORTS FC 26",
                DataVersion = (int)ProfileVersion.FC26,
                CacheName = "fc26",
                Deobfuscator = "NullDeobfuscator",
                AssetLoader = "FC26AssetLoader",
                SDKFilename = "FC26SDK",
                EbxVersion = 4,
                RequiresKey = false,
                MustAddChunks = false,
                EnableExecution = false,
                ContainsEAC = true,
                Banner = new byte[0],
                DefaultDiffuse = "",
                DefaultNormals = "",
                DefaultMask = "",
                DefaultTint = "",
                Sources = new List<FileSystemSource>
                {
                    new FileSystemSource { Path = "Data", SubDirs = false },
                    new FileSystemSource { Path = "Patch", SubDirs = false }
                },
                SharedBundles = new Dictionary<int, string>(),
                IgnoredResTypes = new List<uint>(),
                ProfileData = this
            };
        }
    }
}
