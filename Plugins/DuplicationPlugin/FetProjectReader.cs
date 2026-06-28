using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DuplicationPlugin
{
    internal static class FetProjectReader
    {
        private const ulong FetMagic = 5498700893333637446uL; // "FIFATOOL"

        public struct AddedEbxInfo
        {
            public string Name;
            public Guid Guid;
        }

        public struct ModifiedEbxInfo
        {
            public string Name;
            public byte[] Data;
        }

        public class FetReadResult
        {
            public List<AddedEbxInfo> AddedEbx = new List<AddedEbxInfo>();
            public List<ModifiedEbxInfo> ModifiedEbx = new List<ModifiedEbxInfo>();
            public uint ProjectVersion;
            public string GameName;
        }

        // Some FET project files have a 556-byte obfuscation preamble.
        // The preamble starts with one of two 4-byte signatures.
        private const uint ObfMagic1 = 30331136u;  // 0x01CE2C00
        private const uint ObfMagic2 = 63885568u;  // 0x03CE2C00
        private const int ObfHeaderSize = 556;

        public static FetReadResult Read(string filename)
        {
            using (FileStream fs = new FileStream(filename, FileMode.Open, FileAccess.Read))
            using (NativeReader r = new NativeReader(fs))
            {
                // Check for obfuscation preamble (556 bytes) before the real FET magic.
                uint first4 = r.ReadUInt();
                long bodyOffset = 0;
                if (first4 == ObfMagic1 || first4 == ObfMagic2)
                {
                    bodyOffset = ObfHeaderSize;
                    r.Position = bodyOffset;
                }
                else
                {
                    r.Position = 0;
                }

                ulong magic = r.ReadULong();
                if (magic != FetMagic)
                {
                    App.Logger.Log("FET import: unexpected magic 0x{0:X16} (first4=0x{1:X8}) in {2}",
                        magic, first4, System.IO.Path.GetFileName(filename));

                    // "FETP" magic = newer FET format used in FC 25/26.
                    // Bytes 0-3 spell "FETP" in ASCII (little-endian uint = 0x50544546).
                    const uint FetpSignature = 0x50544546u; // "FETP"
                    if (first4 == FetpSignature)
                    {
                        // Header layout: "FETP"(4) + version(4) + unknown(1) + gameName(LPS-1: 1-byte len + chars)
                        // r.Position is at 8 (after ReadULong consumed bytes 0-7 from pos 0)
                        string gameNameInFile = "FC 25/FC 26";
                        try
                        {
                            r.ReadByte(); // unknown byte at offset 8
                            int gnLen = r.ReadByte(); // game name length at offset 9
                            if (gnLen > 0 && gnLen < 32)
                                gameNameInFile = Encoding.UTF8.GetString(r.ReadBytes(gnLen));
                        }
                        catch { /* best-effort; use default name */ }

                        throw new InvalidDataException(
                            "This .fifaproject file was created by FIFA Editor Tool for " + gameNameInFile + ", not FIFA 17.\n\n" +
                            "The \"FETP\" format used by newer FET versions is not supported by this importer.\n\n" +
                            "This importer only supports the classic \"FIFATOOL\" format produced by FET for FIFA 17 – FIFA 23.");
                    }

                    throw new InvalidDataException(string.Format(
                        "Not a recognised FET .fifaproject file (magic=0x{0:X16}).", magic));
                }

                uint version = r.ReadUInt();

                if (version < 9)
                    throw new InvalidDataException("FET project version " + version + " is too old.");

                // v19+ has an extra headerVersion field
                if (version > 18)
                    r.ReadUInt(); // headerVersion (ignored)

                string gameName;
                if (version <= 15)
                {
                    gameName = ReadNullTerminated(r);
                }
                else
                {
                    gameName = ReadLPS(r);
                }

                // creationDate (ticks + offsetTicks)
                r.ReadLong(); r.ReadLong();
                // modifiedDate (ticks + offsetTicks)
                r.ReadLong(); r.ReadLong();
                // gameVersion
                r.ReadUInt();

                // Header sections
                SkipModSettings(r, version);
                if (version >= 17) SkipLocaleIniSettings(r);
                if (version >= 20) SkipInitFsSettings(r);
                if (version >= 22) SkipPlayerLuaMods(r, version);
                if (version >= 23) SkipPlayerKitLuaMods(r, version);

                // Body
                var result = new FetReadResult { ProjectVersion = version, GameName = gameName };
                ReadBody(r, result, version);
                return result;
            }
        }

        // ── Header skippers ──────────────────────────────────────────────────────

        private static void SkipModSettings(NativeReader r, uint version)
        {
            if (version <= 15)
            {
                ReadNullTerminated(r); // Title
                ReadNullTerminated(r); // Author
                if (version >= 14)
                {
                    r.ReadByte(); // MainCategory
                    r.ReadByte(); // SubCategoryId
                }
                ReadNullTerminated(r); // CustomCategory
                if (version >= 15)
                    ReadNullTerminated(r); // SecondCustomCategory
                ReadNullTerminated(r); // Version
                ReadNullTerminated(r); // Description
            }
            else
            {
                // v16+: 16 LPS strings
                ReadLPS(r); ReadLPS(r); // Title, Author
                r.ReadByte(); r.ReadByte(); // MainCategory, SubCategoryId
                ReadLPS(r); ReadLPS(r); ReadLPS(r); ReadLPS(r); // Custom..., Version, Description
                ReadLPS(r); ReadLPS(r); ReadLPS(r); ReadLPS(r); // links x4
                ReadLPS(r); ReadLPS(r); ReadLPS(r); ReadLPS(r); // links x4
            }

            // Icon
            int iconLen = r.ReadInt();
            if (iconLen > 0) r.ReadBytes(iconLen);

            // Screenshots
            uint screenshotCount = (version < 13) ? 4u : r.ReadUInt();
            for (uint i = 0; i < screenshotCount; i++)
            {
                int ssLen = r.ReadInt();
                if (ssLen > 0) r.ReadBytes(ssLen);
            }
        }

        private static void SkipLocaleIniSettings(NativeReader r)
        {
            int count = r.Read7BitEncodedInt();
            for (int i = 0; i < count; i++)
            {
                ReadLPS(r); // description
                ReadLPS(r); // contents
            }
        }

        private static void SkipInitFsSettings(NativeReader r)
        {
            int fileCount = r.Read7BitEncodedInt();
            for (int i = 0; i < fileCount; i++)
            {
                ReadLPS(r); // description
                int modCount = r.Read7BitEncodedInt();
                for (int j = 0; j < modCount; j++)
                {
                    ReadLPS(r); // filename
                    int byteCount = r.Read7BitEncodedInt();
                    r.ReadBytes(byteCount); // file bytes
                }
            }
        }

        private static void SkipPlayerLuaMods(NativeReader r, uint version)
        {
            if (version >= 26 && version <= 35)
            {
                SkipLuaLists(r, GetPlayerLuaListNames(version));
            }
            else if (version >= 36)
            {
                // default case: count + list of (key + items)
                int count = r.Read7BitEncodedInt();
                for (int i = 0; i < count; i++)
                {
                    ReadLPS(r); // modKey
                    int items = r.Read7BitEncodedInt();
                    for (int j = 0; j < items; j++)
                        ReadLPS(r);
                }
            }
            else
            {
                // v22-25: dictionaries and lists
                SkipLuaDictsAndListsV22_25(r, version, false);
            }
        }

        private static void SkipPlayerKitLuaMods(NativeReader r, uint version)
        {
            if (version >= 26 && version <= 35)
            {
                SkipLuaLists(r, GetPlayerKitLuaListNames(version));
            }
            else if (version >= 36)
            {
                int count = r.Read7BitEncodedInt();
                for (int i = 0; i < count; i++)
                {
                    ReadLPS(r); // modKey
                    int items = r.Read7BitEncodedInt();
                    for (int j = 0; j < items; j++)
                        ReadLPS(r);
                }
            }
            else
            {
                // v23-25: dictionaries
                SkipKitLuaDictsV23_25(r, version);
            }
        }

        private static void SkipList(NativeReader r)
        {
            int count = r.Read7BitEncodedInt();
            for (int i = 0; i < count; i++) ReadLPS(r);
        }

        private static void SkipDict(NativeReader r)
        {
            int count = r.Read7BitEncodedInt();
            for (int i = 0; i < count; i++) { ReadLPS(r); ReadLPS(r); }
        }

        private static void SkipLuaLists(NativeReader r, string[] names)
        {
            foreach (string _ in names) SkipList(r);
        }

        private static string[] GetPlayerLuaListNames(uint version)
        {
            var names = new List<string> {
                "Faces","AlternateFaces","GenericFaces","BodyType","SkinTone",
                "TattooLeftArm","TattooRightArm","TattooFront","TattooBack",
                "ManagerFaces","RefereeFaces","Boots","TeamKits","ManagerSelection",
                "Kits","AlternateKits","ManagerDetails","RefereeDetails","PlayerDetails",
                "TattooLeftLeg","TattooRightLeg","ManagerTattoos"
            };
            if (version >= 27) names.Add("ManagerAccessories");
            if (version >= 29) { names.Add("DynamicYouthSystemEnable"); names.Add("DynamicAppearanceSystemEnable"); names.Add("DynamicBootsSystemEnable"); }
            if (version >= 30) names.Add("ManagerSelectionAccessories");
            if (version == 31) names.Add("SockLengths");
            if (version >= 32) { names.Add("AlternateBoots"); names.Add("PlayerCareerTattoos"); }
            if (version >= 33) { names.Add("PlayerDetailsPerAge"); names.Add("Sleeves"); }
            if (version >= 34) names.Add("GkGloves");
            if (version >= 35) names.Add("KitFonts");
            return names.ToArray();
        }

        private static string[] GetPlayerKitLuaListNames(uint version)
        {
            var names = new List<string> {
                "WarmupKits","ArmTattoos","LegTattoos","WarmAccessoriesPlayer","WarmAccessoriesManager",
                "ColdAccessoriesPlayer","ColdAccessoriesManager","Top0","Top1","Bottom1","TattooId",
                "WarmOutfits","ColdOutfits","ManagerSelectionWarmOutfits","ManagerSelectionColdOutfits"
            };
            if (version >= 28) names.Add("AccessoryAssets");
            if (version >= 31) names.Add("Socks");
            if (version >= 32) { names.Add("SockLengths"); names.Add("Shoes"); }
            if (version >= 33) names.Add("SleeveAssets");
            if (version >= 34) names.Add("GKGloveAssets");
            if (version >= 35) names.Add("Armbands");
            return names.ToArray();
        }

        private static void SkipLuaDictsAndListsV22_25(NativeReader r, uint version, bool kitLua)
        {
            // v22-25 player lua mods: fixed set of dictionaries
            SkipDict(r); SkipDict(r); SkipList(r); // Faces, AlternateFaces, GenericFaces
            SkipDict(r); SkipDict(r); // BodyType, SkinTone
            SkipDict(r); SkipDict(r); SkipDict(r); SkipDict(r); // Tattoos
            SkipDict(r); SkipDict(r); // ManagerFaces, RefereeFaces
            SkipDict(r); SkipDict(r); // Boots, TeamKits
            if (version < 24)
                SkipDict(r); // ManagerSelection (skipped)
            else
                SkipDict(r); // ManagerSelection
            SkipDict(r); SkipDict(r); // Kits, AlternateKits
            if (version >= 24)
            {
                SkipDict(r); SkipDict(r); // WarmOutfits x2
                SkipDict(r); SkipDict(r); // ManagerDetails, RefereeDetails
                SkipDict(r); // PlayerDetails
                SkipDict(r); SkipDict(r); // TattooLeftLeg, TattooRightLeg
            }
            if (version >= 25) SkipDict(r); // ManagerTattoos
        }

        private static void SkipKitLuaDictsV23_25(NativeReader r, uint version)
        {
            SkipDict(r); // WarmupKits
            if (version == 23) SkipDict(r); // skip empty
            if (version >= 24)
            {
                SkipDict(r); SkipDict(r); // ArmTattoos, LegTattoos
                SkipDict(r); SkipDict(r); // WarmAccessoriesPlayer, WarmAccessoriesManager
                SkipDict(r); SkipDict(r); // ColdAccessoriesPlayer, ColdAccessoriesManager
            }
            if (version >= 25)
            {
                SkipDict(r); SkipDict(r); // Top0, Top1
                SkipDict(r); SkipDict(r); // Bottom1, TattooId
            }
        }

        // ── Body ────────────────────────────────────────────────────────────────

        private static void ReadBody(NativeReader r, FetReadResult result, uint version)
        {
            bool useLps = version >= 16;

            // Added bundles (skip)
            int bundleCount = r.ReadInt();
            for (int i = 0; i < bundleCount; i++)
            {
                ReadStr(r, useLps); // name
                ReadStr(r, useLps); // sbname
                r.ReadInt();        // type
            }

            // Added EBX
            int addedEbxCount = r.ReadInt();
            for (int i = 0; i < addedEbxCount; i++)
            {
                string name = ReadStr(r, useLps);
                Guid guid = ReadGuid(r);
                result.AddedEbx.Add(new AddedEbxInfo { Name = name, Guid = guid });
            }

            // Added Res (skip)
            int addedResCount = r.ReadInt();
            for (int i = 0; i < addedResCount; i++)
            {
                ReadStr(r, useLps); // name
                r.ReadULong();      // ResRid
                r.ReadUInt();       // ResType
                r.ReadBytes(16);    // ResMeta
            }

            // Added Chunks (skip)
            int addedChunkCount = r.ReadInt();
            for (int i = 0; i < addedChunkCount; i++)
            {
                ReadGuid(r);  // Id
                r.ReadInt();  // H32
            }

            // Modified EBX
            int modEbxCount = r.ReadInt();
            for (int i = 0; i < modEbxCount; i++)
            {
                string name = ReadStr(r, useLps);
                SkipLinkedAssets(r, useLps);
                bool hasData = r.ReadBoolean();
                if (!hasData) continue;

                r.ReadBoolean(); // isTransientModified
                if (version < 37)
                    ReadNullTerminated(r); // type hint
                if (version >= 18)
                {
                    r.ReadUInt();   // gamePatchVersion
                    r.ReadLong();   // modDateTime ticks
                    r.ReadLong();   // modDateTime offset
                    r.ReadBytes(20); // origSha1
                }

                // added bundles list
                int abCount = r.ReadInt();
                for (int j = 0; j < abCount; j++)
                    ReadStr(r, useLps);

                int dataLen = r.ReadInt();
                byte[] data = r.ReadBytes(dataLen);
                result.ModifiedEbx.Add(new ModifiedEbxInfo { Name = name, Data = data });
            }
        }

        private static void SkipLinkedAssets(NativeReader r, bool useLps)
        {
            int count = r.ReadInt();
            for (int i = 0; i < count; i++)
            {
                string type = ReadStr(r, useLps);
                if (string.Equals(type, "Chunk", StringComparison.OrdinalIgnoreCase))
                    ReadGuid(r);
                else
                    ReadStr(r, useLps); // name
            }
        }

        // ── Primitives ───────────────────────────────────────────────────────────

        private static string ReadStr(NativeReader r, bool useLps)
            => useLps ? ReadLPS(r) : ReadNullTerminated(r);

        private static string ReadLPS(NativeReader r)
        {
            int byteCount = r.Read7BitEncodedInt();
            if (byteCount <= 0) return string.Empty;
            return Encoding.UTF8.GetString(r.ReadBytes(byteCount));
        }

        private static string ReadNullTerminated(NativeReader r)
        {
            var sb = new StringBuilder();
            byte b;
            while ((b = r.ReadByte()) != 0)
                sb.Append((char)b);
            return sb.ToString();
        }

        private static Guid ReadGuid(NativeReader r)
        {
            return new Guid(r.ReadBytes(16));
        }
    }
}
