using FrostySdk;
using FrostySdk.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DuplicationPlugin
{
    // Reader for the newer "FETP" .fifaproject format used by FIFA Editor Tool
    // for FC 24/25/26 (project version 2). Format reverse-engineered from
    // Fifa_Tool.EditorProject in the FC 26 FET build.
    //
    // Layout:
    //   "FETP" (uint32) + projectVersion (byte) + toolVersion (4 bytes)
    //   gameName (LPS) + gameVersion (uint24)
    //   ModSettings, LocaleIni, InitFs, PlayerLua, PlayerKitLua (skipped)
    //   bundles (uint24 count), chunks (uint24), res (uint24), ebx (uint24)
    //
    // All strings are 7-bit-length-prefixed UTF-8 ("LPS"). Asset data blobs are
    // stored CAS-compressed, identical to what Frosty keeps in ModifiedAssetEntry.Data.
    internal static class FetpProjectReader
    {
        public const uint FetpMagic = 0x50544546u; // "FETP"

        [Flags]
        public enum ChunkFlags : ushort
        {
            IsAdded = 1, IsLegacy = 2, AddToSuperBundle = 4,
            HasLogicalOffset = 8, HasLogicalSize = 0x10, HasH32 = 0x20,
            IsLegacyAdded = 0x40, HasAddedBundles = 0x80, BundleAssignmentOnly = 0x100
        }

        [Flags]
        public enum ResFlags : byte
        {
            IsAdded = 1, IsDirectlyModified = 2, HasMeta = 4,
            HasLinkedAssets = 8, HasAddedBundles = 0x10, BundleAssignmentOnly = 0x20
        }

        [Flags]
        public enum EbxFlags : byte
        {
            IsAdded = 1, IsDirectlyModified = 2, AddToBundleRefTable = 4,
            HasLinkedAssets = 8, HasAddedBundles = 0x10, BundleAssignmentOnly = 0x20,
            HasParentBundleRef = 0x40, BundleRefOnly = 0x80
        }

        public class FetpBundle
        {
            public string Name;
            public uint SuperBundleHash;
            public byte Type;
        }

        public class FetpLinkedAsset
        {
            public byte AssetType; // 0=Ebx 1=Res 2=Chunk 3=Legacy
            public string Name;    // ebx/res
            public Guid ChunkId;   // chunk
            public ulong NameHash; // legacy
        }

        public class FetpChunk
        {
            public Guid Id;
            public ChunkFlags Flags;
            public byte[] Sha1;
            public uint LogicalOffset;
            public uint LogicalSize;
            public ulong H32;
            public ulong LegacyNameHash;
            public string LegacyName;
            public uint SuperBundleHash;
            public byte CompressionLevel;
            public byte[] Data;
            public bool IsAdded => (Flags & ChunkFlags.IsAdded) != 0;
        }

        public class FetpRes
        {
            public string Name;
            public ResFlags Flags;
            public uint ResType;
            public ulong ResRid;
            public byte[] Sha1;
            public uint OriginalSize;
            public byte[] Meta;
            public byte CompressionLevel;
            public byte[] Data;
            public List<FetpLinkedAsset> LinkedAssets = new List<FetpLinkedAsset>();
            public bool IsAdded => (Flags & ResFlags.IsAdded) != 0;
            public bool IsDirectlyModified => (Flags & ResFlags.IsDirectlyModified) != 0;
        }

        public class FetpEbx
        {
            public string Name;
            public EbxFlags Flags;
            public string Type;
            public Guid Guid;
            public byte[] Sha1;
            public uint OriginalSize;
            public byte CompressionLevel;
            public byte[] Data;
            public uint BrtNameHash;
            public string BrtPath;
            public string BrtParentPath;
            public List<FetpLinkedAsset> LinkedAssets = new List<FetpLinkedAsset>();
            public bool IsAdded => (Flags & EbxFlags.IsAdded) != 0;
            public bool IsDirectlyModified => (Flags & EbxFlags.IsDirectlyModified) != 0;
        }

        public class FetpResult
        {
            public byte ProjectVersion;
            public Version ToolVersion;
            public string GameName;
            public uint GameVersion;
            public List<FetpBundle> Bundles = new List<FetpBundle>();
            public List<FetpChunk> Chunks = new List<FetpChunk>();
            public List<FetpRes> Res = new List<FetpRes>();
            public List<FetpEbx> Ebx = new List<FetpEbx>();
        }

        public static bool IsFetpFile(string filename)
        {
            using (FileStream fs = new FileStream(filename, FileMode.Open, FileAccess.Read))
            {
                if (fs.Length < 4)
                    return false;
                byte[] buf = new byte[4];
                fs.Read(buf, 0, 4);
                return BitConverter.ToUInt32(buf, 0) == FetpMagic;
            }
        }

        public static FetpResult Read(string filename)
        {
            using (NativeReader r = new NativeReader(new FileStream(filename, FileMode.Open, FileAccess.Read)))
            {
                if (r.ReadUInt() != FetpMagic)
                    throw new InvalidDataException("Not a FETP project file.");

                FetpResult result = new FetpResult();
                result.ProjectVersion = r.ReadByte();
                result.ToolVersion = new Version(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
                result.GameName = ReadLps(r);
                result.GameVersion = ReadUInt24(r);

                if (result.ProjectVersion > 2)
                    throw new InvalidDataException(string.Format(
                        "FETP project version {0} is newer than this importer supports (max 2).",
                        result.ProjectVersion));

                SkipModSettings(r);
                SkipLocaleIni(r);
                SkipInitFs(r);
                SkipLuaLists(r); // PlayerLua
                SkipLuaLists(r); // PlayerKitLua

                ReadBundles(r, result);
                ReadChunks(r, result);
                ReadRes(r, result);
                ReadEbx(r, result);

                return result;
            }
        }

        // ── Header sections ─────────────────────────────────────────────────────

        private static void SkipModSettings(NativeReader r)
        {
            ReadLps(r); ReadLps(r); // Title, Author
            r.ReadByte(); r.ReadByte(); // MainCategory, SubCategoryId
            for (int i = 0; i < 12; i++)
                ReadLps(r); // categories, version, description, 8 links
            int iconLen = r.Read7BitEncodedInt();
            if (iconLen > 0) r.ReadBytes(iconLen);
            int screenshots = r.Read7BitEncodedInt();
            for (int i = 0; i < screenshots; i++)
            {
                int len = r.Read7BitEncodedInt();
                if (len > 0) r.ReadBytes(len);
            }
        }

        private static void SkipLocaleIni(NativeReader r)
        {
            int count = r.Read7BitEncodedInt();
            for (int i = 0; i < count; i++)
            {
                ReadLps(r); // description
                ReadLps(r); // contents
            }
        }

        private static void SkipInitFs(NativeReader r)
        {
            int count = r.Read7BitEncodedInt();
            for (int i = 0; i < count; i++)
            {
                ReadLps(r); // filename
                int len = r.Read7BitEncodedInt();
                if (len > 0) r.ReadBytes(len);
            }
        }

        private static void SkipLuaLists(NativeReader r)
        {
            int listCount = r.Read7BitEncodedInt();
            for (int i = 0; i < listCount; i++)
            {
                ReadLps(r); // modKey
                int items = r.Read7BitEncodedInt();
                for (int j = 0; j < items; j++)
                    ReadLps(r);
            }
        }

        // ── Body sections ───────────────────────────────────────────────────────

        private static void ReadBundles(NativeReader r, FetpResult result)
        {
            uint count = ReadUInt24(r);
            for (uint i = 0; i < count; i++)
            {
                result.Bundles.Add(new FetpBundle
                {
                    Name = ReadLps(r),
                    SuperBundleHash = r.ReadUInt(),
                    Type = r.ReadByte()
                });
            }
        }

        private static void ReadChunks(NativeReader r, FetpResult result)
        {
            uint count = ReadUInt24(r);
            for (uint i = 0; i < count; i++)
            {
                FetpChunk chunk = new FetpChunk
                {
                    Id = new Guid(r.ReadBytes(16)),
                    Flags = (ChunkFlags)r.ReadUShort()
                };
                chunk.Sha1 = r.ReadBytes(20);
                if ((chunk.Flags & ChunkFlags.HasLogicalOffset) != 0)
                    chunk.LogicalOffset = (uint)r.Read7BitEncodedInt();
                if ((chunk.Flags & ChunkFlags.HasLogicalSize) != 0)
                    chunk.LogicalSize = (uint)r.Read7BitEncodedInt();
                if ((chunk.Flags & ChunkFlags.HasH32) != 0)
                    chunk.H32 = r.ReadULong(); // FC 26: 64-bit hash
                int dataSize = r.Read7BitEncodedInt();
                if ((chunk.Flags & ChunkFlags.IsLegacy) != 0)
                {
                    chunk.LegacyNameHash = r.ReadULong();
                    if ((chunk.Flags & ChunkFlags.IsLegacyAdded) != 0)
                        chunk.LegacyName = ReadLps(r);
                }
                ReadUInt24(r); // gamePatchVersionAtImport
                if (!chunk.IsAdded)
                    r.ReadBytes(20); // assetSha1AtImport
                if ((chunk.Flags & ChunkFlags.HasAddedBundles) != 0)
                {
                    int bundleCount = r.Read7BitEncodedInt();
                    for (int j = 0; j < bundleCount; j++)
                        r.ReadULong(); // bundle name hash
                }
                if ((chunk.Flags & ChunkFlags.AddToSuperBundle) != 0)
                    chunk.SuperBundleHash = r.ReadUInt();
                chunk.CompressionLevel = r.ReadByte();
                chunk.Data = r.ReadBytes(dataSize);
                result.Chunks.Add(chunk);
            }
        }

        private static void ReadRes(NativeReader r, FetpResult result)
        {
            uint count = ReadUInt24(r);
            for (uint i = 0; i < count; i++)
            {
                FetpRes res = new FetpRes
                {
                    Name = ReadLps(r),
                    Flags = (ResFlags)r.ReadByte()
                };
                if (res.IsDirectlyModified)
                {
                    if (res.IsAdded)
                    {
                        res.ResType = r.ReadUInt();
                        res.ResRid = r.ReadULong();
                    }
                    res.Sha1 = r.ReadBytes(20);
                    res.OriginalSize = (uint)r.Read7BitEncodedInt();
                    if ((res.Flags & ResFlags.HasMeta) != 0)
                        res.Meta = r.ReadBytes(16);
                    ReadUInt24(r); // gamePatchVersionAtImport
                    if (!res.IsAdded)
                        r.ReadBytes(20); // assetSha1AtImport
                    if ((res.Flags & ResFlags.HasAddedBundles) != 0)
                    {
                        int bundleCount = r.Read7BitEncodedInt();
                        for (int j = 0; j < bundleCount; j++)
                            r.ReadULong();
                    }
                    res.CompressionLevel = r.ReadByte();
                    res.Data = r.ReadBytes(r.Read7BitEncodedInt());
                    if ((res.Flags & ResFlags.HasLinkedAssets) != 0)
                        res.LinkedAssets = ReadLinkedAssets(r);
                }
                else if ((res.Flags & ResFlags.HasLinkedAssets) != 0)
                {
                    res.LinkedAssets = ReadLinkedAssets(r);
                }
                result.Res.Add(res);
            }
        }

        private static void ReadEbx(NativeReader r, FetpResult result)
        {
            uint count = ReadUInt24(r);
            for (uint i = 0; i < count; i++)
            {
                FetpEbx ebx = new FetpEbx
                {
                    Name = ReadLps(r),
                    Flags = (EbxFlags)r.ReadByte()
                };
                if (ebx.IsDirectlyModified)
                {
                    if (ebx.IsAdded)
                    {
                        ebx.Type = ReadLps(r);
                        ebx.Guid = new Guid(r.ReadBytes(16));
                    }
                    ReadUInt24(r); // gamePatchVersionAtImport
                    if (!ebx.IsAdded)
                        r.ReadBytes(20); // assetSha1AtImport
                    if ((ebx.Flags & EbxFlags.AddToBundleRefTable) != 0)
                    {
                        ebx.BrtNameHash = r.ReadUInt();
                        ebx.BrtPath = ReadLps(r);
                        if ((ebx.Flags & EbxFlags.HasParentBundleRef) != 0)
                            ebx.BrtParentPath = ReadLps(r);
                    }
                    if ((ebx.Flags & EbxFlags.HasAddedBundles) != 0)
                    {
                        int bundleCount = r.Read7BitEncodedInt();
                        for (int j = 0; j < bundleCount; j++)
                            r.ReadULong();
                    }
                    ebx.Sha1 = r.ReadBytes(20);
                    ebx.OriginalSize = (uint)r.Read7BitEncodedInt();
                    ebx.CompressionLevel = r.ReadByte();
                    ebx.Data = r.ReadBytes(r.Read7BitEncodedInt());
                    if ((ebx.Flags & EbxFlags.HasLinkedAssets) != 0)
                        ebx.LinkedAssets = ReadLinkedAssets(r);
                }
                else if ((ebx.Flags & EbxFlags.HasLinkedAssets) != 0)
                {
                    ebx.LinkedAssets = ReadLinkedAssets(r);
                }
                result.Ebx.Add(ebx);
            }
        }

        private static List<FetpLinkedAsset> ReadLinkedAssets(NativeReader r)
        {
            int count = r.Read7BitEncodedInt();
            List<FetpLinkedAsset> list = new List<FetpLinkedAsset>(count);
            for (int i = 0; i < count; i++)
            {
                FetpLinkedAsset la = new FetpLinkedAsset { AssetType = r.ReadByte() };
                switch (la.AssetType)
                {
                    case 0: // Ebx
                    case 1: // Res
                        la.Name = ReadLps(r);
                        break;
                    case 2: // Chunk
                        la.ChunkId = new Guid(r.ReadBytes(16));
                        break;
                    case 3: // Legacy
                        la.NameHash = r.ReadULong();
                        break;
                    default:
                        throw new InvalidDataException(string.Format(
                            "Unknown linked asset type {0} in FETP project.", la.AssetType));
                }
                list.Add(la);
            }
            return list;
        }

        // ── Primitives ──────────────────────────────────────────────────────────

        private static uint ReadUInt24(NativeReader r)
        {
            return (uint)(r.ReadByte() | (r.ReadByte() << 8) | (r.ReadByte() << 16));
        }

        private static string ReadLps(NativeReader r)
        {
            int byteCount = r.Read7BitEncodedInt();
            if (byteCount <= 0) return string.Empty;
            return Encoding.UTF8.GetString(r.ReadBytes(byteCount));
        }
    }
}
