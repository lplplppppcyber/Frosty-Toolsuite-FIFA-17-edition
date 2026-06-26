using System;
using System.Collections.Generic;
using System.IO;
using FrostySdk.Interfaces;
using FrostySdk.IO;
using System.Security.Cryptography;
using FrostySdk.Managers;
using Frosty.Hash;

namespace FrostySdk
{
    public struct ManifestFileRef
    {
        public int CatalogIndex => (value >> 12) - 1;
        public bool IsInPatch => (value & 0x100) != 0;
        public int CasIndex => (value & 0xFF) + 1;

        private int value;

        public ManifestFileRef(int inIndex, bool inPatch, int inCasIndex)
        {
            value = ((inIndex + 1) << 12) | (inPatch ? 0x100 : 0x00) | ((inCasIndex - 1) & 0xFF);
        }

        public static implicit operator ManifestFileRef(int inValue) => new ManifestFileRef { value = inValue };
        public static implicit operator int(ManifestFileRef inRef)   => inRef.value;
    }

    public class ManifestFileInfo
    {
        public ManifestFileRef file;
        public uint offset;
        public long size;
        public bool isChunk;
    }

    public class ManifestBundleInfo
    {
        public int hash;
        public List<ManifestFileInfo> files = new List<ManifestFileInfo>();
    }

    public class ManifestChunkInfo
    {
        public Guid guid;
        public ManifestFileInfo file;
        public int fileIndex;
    }

    public class CatalogInfo
    {
        public Guid Id;
        public string Name;
        public bool AlwaysInstalled;
        public Dictionary<string, bool> SuperBundles = new Dictionary<string, bool>();
    }

    public class FileSystem
    {
        public int SuperBundleCount => superBundles.Count;

        public IEnumerable<string> SuperBundles
        {
            get
            {
                for (int i = 0; i < superBundles.Count; i++)
                    yield return superBundles[i];
            }
        }

        public int CatalogCount => catalogs.Count;

        public IEnumerable<string> Catalogs
        {
            get
            {
                for (int i = 0; i < catalogs.Count; i++)
                    yield return catalogs[i].Name;
            }
        }

        public int CasFileCount => casFiles.Count;

        public uint Base { get; private set; }
        public uint Head { get; private set; }
        public string CacheName { get; }
        public string BasePath { get; }

        private List<string> paths = new List<string>();
        private List<string> superBundles = new List<string>();
        private List<CatalogInfo> catalogs = new List<CatalogInfo>();
        private Dictionary<string, byte[]> memoryFs = new Dictionary<string, byte[]>();
        private List<string> casFiles = new List<string>();
        private readonly Type deobfuscatorType;
        private Dictionary<uint, int> fnv1CatalogMap = new Dictionary<uint, int>();

        public FileSystem(string inBasePath)
        {
            BasePath = inBasePath;
            if (!BasePath.EndsWith("\\") || !BasePath.EndsWith("/"))
                BasePath += "\\";

            CacheName = "Caches/" + ProfilesLibrary.CacheName;
            deobfuscatorType = ProfilesLibrary.Deobfuscator;
        }

        public void Initialize(byte[] key = null)
        {
            ProcessLayouts();
            LoadInitfs(key);
        }

        public IDeobfuscator CreateDeobfuscator() => (IDeobfuscator)Activator.CreateInstance(deobfuscatorType);

        public void AddSource(string path, bool iterateSubPaths = false)
        {
            if (!Directory.Exists(BasePath + path))
                return;

            if (iterateSubPaths)
            {
                foreach (string subPath in Directory.EnumerateDirectories(BasePath + path, "*", SearchOption.AllDirectories))
                {
                    if (subPath.ToLower().Contains("\\patch"))
                        continue;

                    if (!File.Exists(subPath + "\\package.mft"))
                        continue;

                    paths.Add("\\" + subPath.Replace(BasePath, "").ToLower() + "\\data\\");
                }
            }
            else
                paths.Add("\\" + path.ToLower() + "\\");
        }

        public string ResolvePath(string filename)
        {
#if ENABLE_LCU
            if (ProfilesLibrary.DataVersion == (int)ProfileVersion.Madden20 && filename.StartsWith("LCU/"))
            {
                filename = filename.Replace("LCU/", @"C:\ProgramData\Frostbite\Madden NFL 20\LCU\");
                if (File.Exists(filename))
                    return filename;
                return "";
            }
#endif

            if (filename.StartsWith("native_patch/") && paths.Count == 1)
                return "";

            int startCount = 0;
            int endCount = paths.Count;

            if (filename.StartsWith("native_data/") && paths.Count > 1)
                startCount = 1;
            else if (filename.StartsWith("native_patch/"))
                endCount = 1;

            filename = filename.Replace("native_data/", "");
            filename = filename.Replace("native_patch/", "");
            filename = filename.Trim('/');

            for (int i = startCount; i < endCount; i++)
            {
                if (File.Exists(BasePath + paths[i] + filename) || Directory.Exists(BasePath + paths[i] + filename))
                    return (BasePath + paths[i] + filename).Replace("\\\\", "\\");
            }
            return "";
        }

        public string ResolvePath(ManifestFileRef fileRef)
        {
            string path = (fileRef.IsInPatch ? "native_patch/" : "native_data/") + catalogs[fileRef.CatalogIndex].Name + "/cas_" + fileRef.CasIndex.ToString("D2") + ".cas";
            return ResolvePath(path);
        }

        public string GetCatalogFromSuperBundle(string sbName)
        {
            foreach (CatalogInfo info in catalogs)
            {
                if (info.SuperBundles.ContainsKey(sbName))
                    return info.Name;
            }

            if (ProfilesLibrary.DataVersion == (int)ProfileVersion.Madden19 || ProfilesLibrary.DataVersion == (int)ProfileVersion.Fifa18 || ProfilesLibrary.DataVersion == (int)ProfileVersion.Fifa17)
            {
                // @temp: EA sports (force the first installpackage not lcu)
                foreach (CatalogInfo info in catalogs)
                {
                    if (info.Name.EndsWith("installpackage_00", StringComparison.OrdinalIgnoreCase))
                        return info.Name;
                }
            }
            else
            {
                // return the first catalog with superbundles
                foreach (CatalogInfo info in catalogs)
                {
                    if (info.SuperBundles.Count == 0)
                        continue;
                    return info.Name;
                }
            }

            // fallback just incase
            return catalogs[0].Name;
        }

        public string GetCatalog(ManifestFileRef fileRef) => catalogs[fileRef.CatalogIndex].Name;

        public IEnumerable<CatalogInfo> EnumerateCatalogInfos()
        {
            foreach (CatalogInfo ci in catalogs)
                yield return ci;
        }

        public ManifestFileRef GetFileRef(string path)
        {
            path = path.Replace(BasePath, "");
            foreach (string source in paths)
                path = path.Replace(source, "");

            if (path.EndsWith("cat"))
            {
                path = path.Remove(path.Length - 8);
            }
            else if (path.EndsWith("cas"))
            {
                path = path.Remove(path.Length - 11);
            }

            foreach (CatalogInfo info in catalogs)
            {
                if (info.Name.Equals(path, StringComparison.OrdinalIgnoreCase))
                    return new ManifestFileRef(catalogs.IndexOf(info), false, 0);
            }

            return new ManifestFileRef();
        }

        public bool HasFileInMemoryFs(string name)     =>  memoryFs.ContainsKey(name);
        public byte[] GetFileFromMemoryFs(string name) => !memoryFs.ContainsKey(name) ? null : memoryFs[name];

        public IEnumerable<string> EnumerateFilesInMemoryFs()
        {
            foreach (string key in memoryFs.Keys)
                yield return key;
        }

        public uint GetFsCount() => (uint)memoryFs.Count;

        public string GetFilePath(int index) => index < casFiles.Count ? casFiles[index] : "";

        public string GetFilePath(int catalog, int cas, bool patch)
        {
            CatalogInfo ci = catalogs[catalog];
            return ((patch) ? "native_patch/" : "native_data/") + ci.Name + "/cas_" + cas.ToString("D2") + ".cas";
        }

        public int GetCatalogIndexFromFNV1(uint fnv1) =>
            fnv1CatalogMap.TryGetValue(fnv1, out int idx) ? idx : -1;

        private void LoadInitfs(byte[] key, bool patched = true)
        {
            string path = ResolvePath((patched ? "" : "native_data/") + "initfs_win32");
            if (path == "")
                return;

            using (DbReader reader = new DbReader(new FileStream(path, FileMode.Open, FileAccess.Read), CreateDeobfuscator()))
            {
                DbObject initfs = reader.ReadDbObject();
                if (ProfilesLibrary.DataVersion == (int)ProfileVersion.Fifa18 || ProfilesLibrary.DataVersion == (int)ProfileVersion.Fifa19 || ProfilesLibrary.DataVersion == (int)ProfileVersion.Anthem || ProfilesLibrary.DataVersion == (int)ProfileVersion.Anthem || ProfilesLibrary.DataVersion == (int)ProfileVersion.Fifa20
                 || ProfilesLibrary.DataVersion == (int)ProfileVersion.PlantsVsZombiesBattleforNeighborville || ProfilesLibrary.DataVersion == (int)ProfileVersion.NeedForSpeedHeat
                    )
                {
                    byte[] buffer = initfs.GetValue<byte[]>("encrypted");
                    if (buffer != null)
                    {
                        if (key == null)
                            return;

                        // need to decrypt the encrypted block
                        using (Aes aes = Aes.Create())
                        {
                            aes.Key = key;
                            aes.IV = key;

                            ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                            using (MemoryStream decryptStream = new MemoryStream(buffer))
                            {
                                using (CryptoStream cryptoStream = new CryptoStream(decryptStream, decryptor, CryptoStreamMode.Read))
                                    cryptoStream.Read(buffer, 0, buffer.Length);
                            }
                        }

                        // now read in newly decrypted initfs
                        using (DbReader newReader = new DbReader(new MemoryStream(buffer), CreateDeobfuscator()))
                            initfs = newReader.ReadDbObject();
                    }
                }

                // iterate and store files in the memory fs
                if (initfs != null)
                {
                    foreach (DbObject fileStub in initfs)
                    {
                        DbObject file = fileStub.GetValue<DbObject>("$file");
                        string name = file.GetValue<string>("name");

                        if (!memoryFs.ContainsKey(name))
                            memoryFs.Add(name, file.GetValue<byte[]>("payload"));
                    }
                }
            }

            if (memoryFs.ContainsKey("__fsinternal__"))
            {
                DbObject obj = null;
                using (DbReader reader = new DbReader(new MemoryStream(memoryFs["__fsinternal__"]), null))
                    obj = reader.ReadDbObject();

                memoryFs.Remove("__fsinternal__");
                if (obj.GetValue<bool>("inheritContent"))
                    LoadInitfs(key, patched: false);
            }
        }

        private void ProcessLayouts()
        {
            if (ProfilesLibrary.DataVersion == (int)ProfileVersion.FC26)
            {
                ProcessLayoutsFC26();
                return;
            }

            string baseLayoutPath = ResolvePath("native_data/layout.toc");
            string patchLayoutPath = ResolvePath("native_patch/layout.toc");

            // Process base layout.toc
            DbObject baseLayout = null;
            using (DbReader reader = new DbReader(new FileStream(baseLayoutPath, FileMode.Open, FileAccess.Read), CreateDeobfuscator()))
                baseLayout = reader.ReadDbObject();

            foreach (DbObject superBundle in baseLayout.GetValue<DbObject>("superBundles"))
                superBundles.Add(superBundle.GetValue<string>("name").ToLower());

            if (patchLayoutPath != "")
            {
                // Process patch layout.toc
                DbObject patchLayout = null;
                using (DbReader reader = new DbReader(new FileStream(patchLayoutPath, FileMode.Open, FileAccess.Read), CreateDeobfuscator()))
                    patchLayout = reader.ReadDbObject();

                foreach (DbObject superBundle in patchLayout.GetValue<DbObject>("superBundles"))
                {
                    // Merge super bundles
                    string superBundleName = superBundle.GetValue<string>("name").ToLower();
                    if (!superBundles.Contains(superBundleName))
                        superBundles.Add(superBundleName);
                }

                Base = patchLayout.GetValue<uint>("base");
                Head = patchLayout.GetValue<uint>("head");

                ProcessCatalogs(patchLayout);
                ProcessManifest(patchLayout);

#if ENABLE_LCU
                if (ProfilesLibrary.DataVersion == (int)ProfileVersion.Madden20)
                {
                    string lcuLayoutPath = ResolvePath("LCU/layout.toc");
                    using (DbReader reader = new DbReader(new FileStream(lcuLayoutPath, FileMode.Open, FileAccess.Read), CreateDeobfuscator()))
                        patchLayout = reader.ReadDbObject();

                    baseNum = patchLayout.GetValue<uint>("base");
                    headNum = patchLayout.GetValue<uint>("head");

                    ProcessCatalogs(patchLayout);
                }
#endif
            }
            else
            {
                Base = baseLayout.GetValue<uint>("base");
                Head = baseLayout.GetValue<uint>("head");

                ProcessCatalogs(baseLayout);
                ProcessManifest(baseLayout);
            }
        }

        private void ProcessLayoutsFC26()
        {
            // Try common FC26 directory naming conventions (game may use Data/Patch or native_data/native_patch)
            string baseLayoutPath  = FindFC26Layout(false);
            string patchLayoutPath = FindFC26Layout(true);

            var log = new System.Text.StringBuilder();
            log.AppendLine("[FC26] BasePath: " + BasePath);
            log.AppendLine("[FC26] baseLayoutPath: " + (baseLayoutPath == "" ? "<not found>" : baseLayoutPath));
            log.AppendLine("[FC26] patchLayoutPath: " + (patchLayoutPath == "" ? "<not found>" : patchLayoutPath));
            log.AppendLine("[FC26] paths: " + string.Join(", ", paths));

            if (baseLayoutPath == "" && patchLayoutPath == "")
            {
                log.AppendLine("[FC26] ERROR: no layout.toc found in any known directory");
                File.WriteAllText("fc26_debug.txt", log.ToString());
                return;
            }

            FC26TocReader tocReader = new FC26TocReader();

            if (patchLayoutPath != "")
            {
                DbObject patchLayout = tocReader.Read(patchLayoutPath);
                log.AppendLine("[FC26] patchLayout null: " + (patchLayout == null));
                if (patchLayout != null)
                {
                    Base = (uint)patchLayout.GetValue<int>("base");
                    Head = (uint)patchLayout.GetValue<int>("head");
                    log.AppendLine("[FC26] Head=" + Head + " Base=" + Base);
                    ProcessCatalogsFC26(patchLayout, log);
                }
            }
            else
            {
                DbObject baseLayout = tocReader.Read(baseLayoutPath);
                log.AppendLine("[FC26] baseLayout null: " + (baseLayout == null));
                if (baseLayout != null)
                {
                    Base = (uint)baseLayout.GetValue<int>("base");
                    Head = (uint)baseLayout.GetValue<int>("head");
                    log.AppendLine("[FC26] Head=" + Head + " Base=" + Base);
                    ProcessCatalogsFC26(baseLayout, log);
                }
            }

            log.AppendLine("[FC26] catalogs count: " + catalogs.Count);
            log.AppendLine("[FC26] superBundles count: " + superBundles.Count);
            foreach (var cat in catalogs)
                log.AppendLine("[FC26] catalog: " + cat.Name + " sb=" + cat.SuperBundles.Count);

            File.WriteAllText("fc26_debug.txt", log.ToString());
        }

        // Finds layout.toc for FC26 by trying multiple known directory names.
        private string FindFC26Layout(bool patch)
        {
            string[] dirs = patch
                ? new[] { "Patch", "native_patch", "patch" }
                : new[] { "Data", "native_data", "data" };

            // First try via ResolvePath (respects Sources order)
            string resolved = ResolvePath(patch ? "native_patch/layout.toc" : "native_data/layout.toc");
            if (resolved != "" && File.Exists(resolved))
                return resolved;

            // Fall back to direct path search from BasePath
            foreach (string dir in dirs)
            {
                string p = BasePath + dir + "\\layout.toc";
                if (File.Exists(p))
                    return p;
            }

            return "";
        }

        private void ProcessCatalogsFC26(DbObject layout, System.Text.StringBuilder log = null)
        {
            log?.AppendLine("[FC26] layout keys: " + string.Join(", ", layout.EnumerateKeys()));
            DbObject installManifest = layout.GetValue<DbObject>("installManifest");
            if (installManifest == null)
            {
                log?.AppendLine("[FC26] installManifest is null");
                CatalogInfo ci = new CatalogInfo() { Name = "" };
                foreach (string sbName in superBundles)
                    ci.SuperBundles.Add(sbName, false);
                catalogs.Add(ci);
                return;
            }

            log?.AppendLine("[FC26] installManifest keys: " + string.Join(", ", installManifest.EnumerateKeys()));
            DbObject installChunks = installManifest.GetValue<DbObject>("installChunks");
            if (installChunks == null)
            {
                log?.AppendLine("[FC26] installChunks is null");
                return;
            }
            log?.AppendLine("[FC26] installChunks count: " + installChunks.Count);

            foreach (DbObject installChunk in installChunks)
            {
                if (installChunk.GetValue<bool>("testDLC"))
                    continue;

                bool alwaysInstalled = installChunk.GetValue<bool>("alwaysInstalled");
                string path = "win32/" + installChunk.GetValue<string>("name");

                Guid catalogId = installChunk.GetValue<Guid>("id");

                CatalogInfo info = catalogs.Find((CatalogInfo ci) => ci.Id == catalogId);
                if (info == null)
                {
                    info = new CatalogInfo
                    {
                        Id             = catalogId,
                        Name           = path,
                        AlwaysInstalled = alwaysInstalled
                    };

                    DbObject sbList = installChunk.GetValue<DbObject>("superBundles");
                    if (sbList != null)
                    {
                        foreach (string sbName in sbList)
                        {
                            string lower = sbName.ToLower();
                            info.SuperBundles[lower] = false;
                            if (!superBundles.Contains(lower))
                                superBundles.Add(lower);
                        }
                    }

                    DbObject splitSbs = installChunk.GetValue<DbObject>("splitSuperBundles");
                    if (splitSbs != null)
                    {
                        foreach (DbObject sbc in splitSbs)
                        {
                            string sbName = sbc.GetValue<string>("superBundle").ToLower();
                            if (!info.SuperBundles.ContainsKey(sbName))
                                info.SuperBundles[sbName] = true;
                        }
                    }

                    DbObject splitTocs = installChunk.GetValue<DbObject>("splitTocs");
                    if (splitTocs != null)
                    {
                        foreach (DbObject sbc in splitTocs)
                        {
                            string sbName = "win32/" + sbc.GetValue<string>("superbundle").ToLower();
                            if (!info.SuperBundles.ContainsKey(sbName))
                                info.SuperBundles[sbName] = true;
                        }
                    }
                }

                // Map FNV1 persistentIndex to catalog list position
                int persistentIndex = installChunk.GetValue<int>("persistentIndex");
                fnv1CatalogMap[(uint)persistentIndex] = catalogs.Count;

                catalogs.Add(info);
            }
        }

        private void ProcessCatalogs(DbObject patchLayout)
        {
            // Only if an install manifest exists
            DbObject installManifest = patchLayout.GetValue<DbObject>("installManifest");
            if (installManifest != null)
            {
                foreach (DbObject installChunk in installManifest.GetValue<DbObject>("installChunks"))
                {
                    if (installChunk.GetValue<bool>("testDLC"))
                        continue;

                    bool alwaysInstalled = installChunk.GetValue<bool>("alwaysInstalled");

                    string path = "win32/" + installChunk.GetValue<string>("name");

                    // @hack: Ensure that BFV can pass correctly (catalog doesnt exist)
                    if (ProfilesLibrary.DataVersion == (int)ProfileVersion.Battlefield5 || ProfilesLibrary.DataVersion == (int)ProfileVersion.StarWarsSquadrons)
                        if (path == "win32/installation/default")
                            continue;

                    if (!File.Exists(ResolvePath(path + "/cas.cat")))
                    {
                        // FIFA19 - Doesnt have Cat files
                        if ((!installChunk.HasValue("files") || installChunk.GetValue<DbObject>("files").Count == 0) && (ProfilesLibrary.DataVersion != (int)ProfileVersion.Anthem
                            && ProfilesLibrary.DataVersion != (int)ProfileVersion.PlantsVsZombiesBattleforNeighborville && ProfilesLibrary.DataVersion != (int)ProfileVersion.NeedForSpeedHeat
                            ))
                        {
                            // BFV needs even non existent catalogs to be in the list for indexing to work
                            if (ProfilesLibrary.DataVersion != (int)ProfileVersion.Battlefield5 && ProfilesLibrary.DataVersion != (int)ProfileVersion.StarWarsSquadrons)
                                continue;
                        }
                    }

                    CatalogInfo info = null;
                    Guid catalogId = installChunk.GetValue<Guid>("id");

                    info = catalogs.Find((CatalogInfo ci) => ci.Id == catalogId);
                    if (info == null)
                    {
                        info = new CatalogInfo
                        {
                            Id = installChunk.GetValue<Guid>("id"),
                            Name = path,
                            AlwaysInstalled = alwaysInstalled
                        };

                        foreach (string superBundle in installChunk.GetValue<DbObject>("superBundles"))
                            info.SuperBundles.Add(superBundle.ToLower(), false);
                    }

                    catalogs.Add(info);

                    if (installChunk.HasValue("files"))
                    {
                        foreach (DbObject fileObj in installChunk.GetValue<DbObject>("files"))
                        {
                            int index = fileObj.GetValue<int>("id");
                            while (casFiles.Count <= index)
                                casFiles.Add("");

                            string casPath = fileObj.GetValue<string>("path").Trim('/');
                            casPath = casPath.Replace("native_data/Data", "native_data");
                            casPath = casPath.Replace("native_data/Patch", "native_patch");

                            casFiles[index] = casPath;
                        }
                    }

                    if (installChunk.HasValue("splitSuperBundles"))
                    {
                        foreach (DbObject superBundleContainer in installChunk.GetValue<DbObject>("splitSuperBundles"))
                        {
                            string superBundle = superBundleContainer.GetValue<string>("superBundle").ToLower();
                            if (!info.SuperBundles.ContainsKey(superBundle))
                                info.SuperBundles.Add(superBundle, true);
                        }
                    }

                    if (installChunk.HasValue("splitTocs"))
                    {
                        foreach (DbObject superBundleContainer in installChunk.GetValue<DbObject>("splitTocs"))
                        {
                            string superBundle = "win32/" + superBundleContainer.GetValue<string>("superbundle").ToLower();
                            if (!info.SuperBundles.ContainsKey(superBundle))
                                info.SuperBundles.Add(superBundle, true);
                        }
                    }
                }
            }
            else
            {
                // Otherwise default to /data
                CatalogInfo ci = new CatalogInfo() { Name = "" };
                foreach (string sbName in superBundles)
                    ci.SuperBundles.Add(sbName, false);
                catalogs.Add(ci);
            }
        }

        public IEnumerable<DbObject> EnumerateBundles()
        {
            foreach (ManifestBundleInfo bi in manifestBundles)
            {
                ManifestFileInfo fi = bi.files[0];
                CatalogInfo catalog = catalogs[fi.file.CatalogIndex];

                string path = ResolvePath(fi.file);
                if (!File.Exists(path))
                    continue;

                using (NativeReader reader = new NativeReader(new FileStream(path, FileMode.Open, FileAccess.Read)))
                {
                    using (BinarySbReader sbReader = new BinarySbReader(reader.CreateViewStream(fi.offset, fi.size), 0, null))
                    {
                        DbObject bundle = sbReader.ReadDbObject();
                        System.Diagnostics.Debug.Assert(bundle != null);

                        string name = bi.hash.ToString("x8");
                        if (ProfilesLibrary.SharedBundles.ContainsKey(bi.hash))
                            name = ProfilesLibrary.SharedBundles[bi.hash];
                        bundle.SetValue("name", name);
                        bundle.SetValue("catalog", catalog.Name);

                        yield return bundle;
                    }
                }
            }
        }

        public List<ChunkAssetEntry> ProcessManifestChunks()
        {
            List<ChunkAssetEntry> chunks = new List<ChunkAssetEntry>();
            foreach (ManifestChunkInfo ci in manifestChunks)
            {
                ManifestFileInfo fi = ci.file;
                ChunkAssetEntry entry = new ChunkAssetEntry {Id = ci.guid};

                string path = (fi.file.IsInPatch ? "native_patch/" : "native_data/") + catalogs[fi.file.CatalogIndex].Name + "/cas_" + fi.file.CasIndex.ToString("D2") + ".cas";
                entry.Location = AssetDataLocation.CasNonIndexed;
                entry.Size = fi.size;

                entry.ExtraData = new AssetExtraData
                {
                    DataOffset = fi.offset,
                    CasPath = path
                };

                chunks.Add(entry);
            }

            return chunks;
        }

        private readonly List<ManifestBundleInfo> manifestBundles = new List<ManifestBundleInfo>();
        private readonly List<ManifestChunkInfo> manifestChunks = new List<ManifestChunkInfo>();

        public ManifestBundleInfo GetManifestBundle(string name)
        {
            if (name.Length != 8 || !int.TryParse(name, System.Globalization.NumberStyles.HexNumber, null, out int hash))
                hash = Fnv1.HashString(name);

            foreach (ManifestBundleInfo bi in manifestBundles)
            {
                if (bi.hash == hash)
                    return bi;
            }
            return null;
        }

        public ManifestBundleInfo GetManifestBundle(int nameHash)
        {
            foreach (ManifestBundleInfo bi in manifestBundles)
            {
                if (bi.hash == nameHash)
                    return bi;
            }
            return null;
        }

        public ManifestChunkInfo GetManifestChunk(Guid id)
        {
            return manifestChunks.Find((ManifestChunkInfo a) => a.guid == id);
        }

        public void AddManifestBundle(ManifestBundleInfo bi)
        {
            manifestBundles.Add(bi);
        }

        public void AddManifestChunk(ManifestChunkInfo ci)
        {
            manifestChunks.Add(ci);
        }

        public void ResetManifest()
        {
            manifestBundles.Clear();
            manifestChunks.Clear();
            catalogs.Clear();
            superBundles.Clear();

            ProcessLayouts();
        }

        public byte[] WriteManifest()
        {
            manifestChunks.Sort((a, b) => a.file.file.CatalogIndex.CompareTo(b.file.file.CatalogIndex));
            using (NativeWriter writer = new NativeWriter(new MemoryStream()))
            {
                List<ManifestFileInfo> files = new List<ManifestFileInfo>();
                foreach (ManifestBundleInfo bi in manifestBundles)
                {
                    for (int i = 0; i < bi.files.Count; i++)
                    {
                        ManifestFileInfo fi = bi.files[i];
                        //if (i < bi.files.Count - 2)
                        //{
                        //    ManifestFileInfo nextFi = bi.files[i + 1];
                        //    while (nextFi.offset == (fi.offset + fi.size))
                        //    {
                        //        fi.size += nextFi.size;
                        //        if (i >= bi.files.Count - 2)
                        //            break;
                        //        nextFi = bi.files[++i + 1];
                        //    }
                        //}

                        files.Add(fi);
                    }
                }
                foreach (ManifestChunkInfo ci in manifestChunks)
                {
                    files.Add(ci.file);
                    ci.fileIndex = files.Count - 1;
                }

                writer.Write(files.Count);
                writer.Write(manifestBundles.Count);
                writer.Write(manifestChunks.Count);

                foreach (ManifestFileInfo fi in files)
                {
                    writer.Write(fi.file);
                    writer.Write(fi.offset);
                    writer.Write(fi.size);
                }

                foreach (ManifestBundleInfo bi in manifestBundles)
                {
                    writer.Write(bi.hash);
                    writer.Write(files.IndexOf(bi.files[0]));
                    writer.Write(bi.files.Count);
                    writer.Write(0);
                    writer.Write(0);
                }

                foreach (ManifestChunkInfo ci in manifestChunks)
                {
                    writer.Write(ci.guid);
                    writer.Write(ci.fileIndex);
                }

                return writer.ToByteArray();;
            }
        }

        private void ProcessManifest(DbObject patchLayout)
        {
            DbObject manifest = patchLayout.GetValue<DbObject>("manifest");
            if (manifest != null)
            {
                List<ManifestFileInfo> manifestFiles = new List<ManifestFileInfo>();

                ManifestFileRef file = manifest.GetValue<int>("file");
                CatalogInfo catalog = catalogs[file.CatalogIndex];

                string manifestPath = ResolvePath(file);
                using (NativeReader reader = new NativeReader(new FileStream(manifestPath, FileMode.Open, FileAccess.Read)))
                {
                    long manifestOffset = manifest.GetValue<int>("offset");
                    long manifestSize = manifest.GetValue<int>("size");

                    reader.Position = manifestOffset;

                    uint fileCount = reader.ReadUInt();
                    uint bundleCount = reader.ReadUInt();
                    uint chunksCount = reader.ReadUInt();

                    // files
                    for (uint i = 0; i < fileCount; i++)
                    {
                        ManifestFileInfo fi = new ManifestFileInfo()
                        {
                            file = reader.ReadInt(),
                            offset = reader.ReadUInt(),
                            size = reader.ReadLong(),
                            isChunk = false
                        };
                        manifestFiles.Add(fi);
                    }

                    // bundles
                    for (uint i = 0; i < bundleCount; i++)
                    {
                        ManifestBundleInfo bi = new ManifestBundleInfo {hash = reader.ReadInt()};

                        int startIndex = reader.ReadInt();
                        int count = reader.ReadInt();

                        int unk1 = reader.ReadInt();
                        int unk2 = reader.ReadInt();

                        for (int j = 0; j < count; j++)
                            bi.files.Add(manifestFiles[startIndex + j]);

                        manifestBundles.Add(bi);
                    }

                    // chunks
                    for (uint i = 0; i < chunksCount; i++)
                    {
                        ManifestChunkInfo ci = new ManifestChunkInfo
                        {
                            guid = reader.ReadGuid(),
                            fileIndex = reader.ReadInt()
                        };
                        ci.file = manifestFiles[ci.fileIndex];
                        ci.file.isChunk = true;
                        manifestChunks.Add(ci);
                    }
                }
            }
        }
    }
}
