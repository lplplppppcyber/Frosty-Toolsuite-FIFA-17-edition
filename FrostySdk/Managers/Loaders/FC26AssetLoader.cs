using System;
using System.Collections.Generic;
using System.IO;
using FrostySdk.IO;

namespace FrostySdk.Managers
{
    public partial class AssetManager
    {
        // Asset loader for FC26 (EA SPORTS FC 26 and FC 25).
        // Reads the 556-byte-header TOC format with Huffman-compressed bundle names
        // and FNV1 uint32 catalog indexes.
        internal class FC26AssetLoader : IAssetLoader
        {
            // -----------------------------------------------------------------------
            // Internal data structures for TOC parsing (mirrors FET TocMeta)
            // -----------------------------------------------------------------------

            private struct TocBundleMeta
            {
                public string Name;
                public uint   Length;  // flags 0x80000000/0x40000000 → inline; otherwise real length
                public long   Offset;
            }

            private struct TocChunkMeta
            {
                public Guid Id;
                public int  CatalogIndex;
                public int  CasIndex;
                public bool InPatch;
                public uint DataOffset;
                public uint DataSize;
            }

            private struct CasEntryMeta
            {
                public int  CatalogIndex;
                public int  CasIndex;
                public bool InPatch;
                public uint EntryOffset;
                public int  EntrySize;
            }

            private struct InlineBundleMeta
            {
                public string              Name;
                public int                 CatalogIndex;
                public int                 CasIndex;
                public bool                InPatch;
                public uint                BundleOffset;
                public int                 BundleLength;
                public List<CasEntryMeta>  Entries;  // per-asset CAS refs (indices 1..N-1)
            }

            // -----------------------------------------------------------------------
            // Constants
            // -----------------------------------------------------------------------

            private const uint FlagMemoryResident = 0x80000000u;
            private const uint FlagInlineRead     = 0x40000000u;
            private const int  TocHeaderSize      = 556;
            private const uint BundleMagicF21     = 0xD6A03D9Du;
            private const int  SuperBundleMagic   = 32;

            // -----------------------------------------------------------------------
            // IAssetLoader.Load
            // -----------------------------------------------------------------------

            public void Load(AssetManager parent, BinarySbDataHelper helper)
            {
                var loadLog = new System.Text.StringBuilder();
                int tocFound = 0, tocLoaded = 0;

                // Collect all (catalog, sbName) pairs first so we can report progress
                var work = new List<(CatalogInfo catalog, string sbName)>();
                foreach (CatalogInfo catalog in parent.fs.EnumerateCatalogInfos())
                    foreach (string sbName in catalog.SuperBundles.Keys)
                        work.Add((catalog, sbName));

                int total = work.Count;
                for (int wi = 0; wi < total; wi++)
                {
                    CatalogInfo catalog = work[wi].catalog;
                    string      sbName  = work[wi].sbName;

                    parent.WriteToLog("Building FC26 cache ({0}/{1}): {2}", wi + 1, total, sbName);
                    parent.WriteToLog("progress:{0}", (double)(wi + 1) / total * 100.0);

                    int sbIndex = GetOrAddSuperBundle(parent, sbName);

                    string sbPath = sbName;
                    if (catalog.SuperBundles[sbName])
                        sbPath = sbName.Replace("win32", catalog.Name);

                    string patchToc = parent.fs.ResolvePath("native_patch/" + sbPath + ".toc");
                    string baseToc  = parent.fs.ResolvePath("native_data/"  + sbPath + ".toc");

                    loadLog.AppendLine($"SB={sbName} split={catalog.SuperBundles[sbName]} sbPath={sbPath}");
                    loadLog.AppendLine($"  patchToc={patchToc}");
                    loadLog.AppendLine($"  baseToc={baseToc}");

                    if (patchToc != "") { tocFound++; ReadToc(patchToc, Path.ChangeExtension(patchToc, ".sb"), sbIndex, parent, helper, loadLog, ref tocLoaded); }
                    if (baseToc  != "") { tocFound++; ReadToc(baseToc,  Path.ChangeExtension(baseToc,  ".sb"), sbIndex, parent, helper, loadLog, ref tocLoaded); }
                }

                loadLog.Insert(0, $"[FC26 Loader] tocFound={tocFound} tocLoaded={tocLoaded} bundles={parent.bundles.Count}\n");
                System.IO.File.WriteAllText("fc26_loader_debug.txt", loadLog.ToString());
            }

            // -----------------------------------------------------------------------
            // ReadToc: parse one .toc + .sb pair
            // -----------------------------------------------------------------------

            private void ReadToc(string tocPath, string sbPath, int sbIndex,
                                  AssetManager parent, BinarySbDataHelper helper,
                                  System.Text.StringBuilder log, ref int loaded)
            {
                List<TocBundleMeta>    bundles    = new List<TocBundleMeta>();
                List<TocChunkMeta>     tocChunks  = new List<TocChunkMeta>();
                List<InlineBundleMeta> casBundles = new List<InlineBundleMeta>();
                int fnvLogCount = 0;

                // ----------------------------------------------------------------
                // Parse TOC file
                // ----------------------------------------------------------------
                using (FileStream tocFs = new FileStream(tocPath, FileMode.Open, FileAccess.Read))
                {
                    NativeReader r = new NativeReader(tocFs);
                    r.Position = TocHeaderSize;

                    uint bundleTableOff = r.ReadUInt(Endian.Big);
                    log?.AppendLine($"  ReadToc {System.IO.Path.GetFileName(tocPath)}: bundleTableOff={bundleTableOff}");
                    if (bundleTableOff != 60)
                    {
                        log?.AppendLine($"    -> SKIPPED (expected 60)");
                        return; // not a valid FC26 TOC
                    }
                    loaded++;

                    uint bundleDataOff  = r.ReadUInt(Endian.Big);
                    int  bundleCount    = r.ReadInt(Endian.Big);
                    uint chunkTableOff  = r.ReadUInt(Endian.Big);
                    uint chunkGuidOff   = r.ReadUInt(Endian.Big);
                    int  chunkCount     = r.ReadInt(Endian.Big);
                    r.ReadUInt(Endian.Big); // unk
                    r.ReadUInt(Endian.Big); // unk
                    int  namesOffset    = r.ReadInt(Endian.Big);
                    uint dataOffset     = r.ReadUInt(Endian.Big);
                    r.ReadInt(Endian.Big);  // unk
                    int  tocFlags       = r.ReadInt(Endian.Big);

                    bool compressedStrings = (tocFlags & 4) != 0;
                    log?.AppendLine($"    bundleCount={bundleCount} chunkCount={chunkCount} tocFlags={tocFlags} bundleDataOff={bundleDataOff} dataOffset={dataOffset} namesOffset={namesOffset} compressedStrings={compressedStrings}");

                    int namesCount          = 0;
                    int decodeTableSize     = 0;
                    int decodeTableOffset   = 0;
                    FC26HuffmanDecoder huffman = null;

                    if (compressedStrings)
                    {
                        namesCount        = r.ReadInt(Endian.Big);
                        decodeTableSize   = r.ReadInt(Endian.Big);
                        decodeTableOffset = r.ReadInt(Endian.Big);
                    }

                    // Build Huffman decoder
                    if (bundleCount > 0 && compressedStrings)
                    {
                        r.Position = TocHeaderSize + namesOffset;
                        uint[] namesData   = FC26HuffmanDecoder.ReadData(r, namesCount);
                        r.Position = TocHeaderSize + decodeTableOffset;
                        int[]  decodeTable = FC26HuffmanDecoder.ReadTable(r, decodeTableSize);
                        huffman = new FC26HuffmanDecoder(decodeTable, namesData);
                    }

                    // Read bundle table (index offsets – not needed, but advance past them)
                    if (bundleCount > 0)
                    {
                        r.Position = TocHeaderSize + bundleTableOff;
                        for (int i = 0; i < bundleCount; i++)
                            r.ReadInt(Endian.Big);

                        // Read bundle data: nameOffset | length | offset
                        r.Position = TocHeaderSize + bundleDataOff;
                        for (int i = 0; i < bundleCount; i++)
                        {
                            int  nameOff = r.ReadInt(Endian.Big);
                            uint len     = r.ReadUInt(Endian.Big);
                            long off     = r.ReadLong(Endian.Big);

                            string name = (compressedStrings && huffman != null)
                                ? huffman.ReadString(nameOff)
                                : string.Empty;

                            bundles.Add(new TocBundleMeta { Name = name, Length = len, Offset = off });
                        }
                    }

                    // Read chunk table + guid table + data
                    if (chunkCount > 0)
                    {
                        if (chunkTableOff != 0)
                        {
                            r.Position = TocHeaderSize + chunkTableOff;
                            for (int i = 0; i < chunkCount; i++)
                                r.ReadInt(Endian.Big);
                        }

                        // Chunk guid + order (16 bytes reversed LE guid + uint32 order)
                        r.Position = TocHeaderSize + chunkGuidOff;
                        Guid[] guids   = new Guid[chunkCount];
                        for (int i = 0; i < chunkCount; i++)
                        {
                            byte[] raw = r.ReadBytes(16);
                            Array.Reverse(raw);
                            guids[i] = new Guid(raw);
                            r.ReadUInt(Endian.Big); // decodeAndOrder – not needed for Frosty
                        }

                        // Chunk CAS data: skip byte | patch bool | fnv1 uint32 | extra byte | cas | offset | size
                        r.Position = TocHeaderSize + dataOffset;
                        for (int i = 0; i < chunkCount; i++)
                        {
                            r.ReadByte();
                            bool inPatch = r.ReadBoolean();
                            uint fnv1    = r.ReadUInt(Endian.Big);
                            r.ReadByte(); // extra byte (FC26 FNV1 format)
                            int  cas     = r.ReadByte();
                            uint off     = r.ReadUInt(Endian.Big);
                            uint size    = r.ReadUInt(Endian.Big);

                            int catIdx = parent.fs.GetCatalogIndexFromFNV1(fnv1);
                            if (catIdx < 0)
                                continue;

                            tocChunks.Add(new TocChunkMeta
                            {
                                Id           = guids[i],
                                CatalogIndex = catIdx,
                                CasIndex     = cas,
                                InPatch      = inPatch,
                                DataOffset   = off,
                                DataSize     = size
                            });
                        }
                    }

                    // Process inline (CAS) bundles – those with MemoryResident/InlineRead flags
                    foreach (TocBundleMeta bundle in bundles)
                    {
                        if ((bundle.Length & FlagMemoryResident) == 0 &&
                            (bundle.Length & FlagInlineRead) == 0)
                            continue;

                        r.Position = TocHeaderSize + bundle.Offset;
                        long startPos = r.Position;

                        r.ReadInt(Endian.Big); // unk1
                        r.ReadInt(Endian.Big); // unk2
                        int flagsOff   = r.ReadInt(Endian.Big);
                        int entryCount = r.ReadInt(Endian.Big);
                        int entriesOff = r.ReadInt(Endian.Big);
                        r.ReadInt(Endian.Big);
                        r.ReadInt(Endian.Big);
                        r.ReadInt(Endian.Big);
                        // FC25 has one extra int here; FC26 does not

                        r.Position = startPos + flagsOff;
                        byte[] flags = r.ReadBytes(entryCount);

                        r.Position = startPos + entriesOff;

                        bool isInPatch  = false;
                        int  catIdx     = 0;
                        int  casIdx     = 0;

                        InlineBundleMeta ib = new InlineBundleMeta
                        {
                            Name    = bundle.Name,
                            Entries = new List<CasEntryMeta>()
                        };
                        bool baseSet = false;

                        for (int j = 0; j < entryCount; j++)
                        {
                            if (flags[j] == 128) // new catalog entry (FC26 FNV1 mode)
                            {
                                r.ReadByte();
                                isInPatch = r.ReadBoolean();
                                uint fnv1 = r.ReadUInt(Endian.Big);
                                r.ReadByte(); // extra byte
                                casIdx = r.ReadByte();
                                int ci = parent.fs.GetCatalogIndexFromFNV1(fnv1);
                                if (fnvLogCount < 3)
                                {
                                    log?.AppendLine($"    fnv1=0x{fnv1:X8} ({fnv1}) -> catIdx={ci} casIdx={casIdx} patch={isInPatch}");
                                    fnvLogCount++;
                                }
                                catIdx = ci >= 0 ? ci : 0;
                            }

                            uint entryOff  = r.ReadUInt(Endian.Big);
                            int  entrySize = r.ReadInt(Endian.Big);

                            if (!baseSet)
                            {
                                ib.CatalogIndex = catIdx;
                                ib.CasIndex     = casIdx;
                                ib.InPatch      = isInPatch;
                                ib.BundleOffset = entryOff;
                                ib.BundleLength = entrySize;
                                baseSet = true;
                            }
                            else
                            {
                                ib.Entries.Add(new CasEntryMeta
                                {
                                    CatalogIndex = catIdx,
                                    CasIndex     = casIdx,
                                    InPatch      = isInPatch,
                                    EntryOffset  = entryOff,
                                    EntrySize    = entrySize
                                });
                            }
                        }

                        casBundles.Add(ib);
                    }
                }

                // ----------------------------------------------------------------
                // Non-inline bundles from .sb file (rare / possibly unused in FC26)
                // ----------------------------------------------------------------
                foreach (TocBundleMeta bundle in bundles)
                {
                    if ((bundle.Length & FlagMemoryResident) != 0 ||
                        (bundle.Length & FlagInlineRead) != 0)
                        continue; // handled above as inline
                    if (bundle.Length > int.MaxValue || !File.Exists(sbPath))
                        continue;

                    int len = (int)bundle.Length;
                    byte[] data = new byte[len];
                    using (FileStream sbFs = new FileStream(sbPath, FileMode.Open, FileAccess.Read))
                    {
                        sbFs.Seek(bundle.Offset, SeekOrigin.Begin);
                        if (sbFs.Read(data, 0, len) != len)
                            continue;
                    }

                    DbObject dbObj;
                    using (MemoryStream ms = new MemoryStream(data))
                        dbObj = ReadSbBundle(ms);
                    if (dbObj == null)
                        continue;

                    parent.bundles.Add(new BundleEntry { Name = bundle.Name, SuperBundleId = sbIndex });
                    int bid = parent.bundles.Count - 1;
                    parent.ProcessBundleEbx(dbObj, bid, helper);
                    parent.ProcessBundleRes(dbObj, bid, helper);
                    parent.ProcessBundleChunks(dbObj, bid, helper);
                }

                // ----------------------------------------------------------------
                // CAS (inline) bundles
                // ----------------------------------------------------------------
                int casNoResolve = 0, casBadLen = 0, casShortRead = 0, casNullObj = 0, casOk = 0;
                foreach (InlineBundleMeta ib in casBundles)
                {
                    string casPath = parent.fs.GetFilePath(ib.CatalogIndex, ib.CasIndex, ib.InPatch);
                    string resolved = parent.fs.ResolvePath(casPath);
                    if (string.IsNullOrEmpty(resolved) || !File.Exists(resolved))
                    {
                        casNoResolve++;
                        if (casNoResolve <= 2) log?.AppendLine($"    cas NORESOLVE cat={ib.CatalogIndex} cas={ib.CasIndex} patch={ib.InPatch} path={casPath} resolved={resolved}");
                        continue;
                    }

                    int  blen = ib.BundleLength - 4;
                    long boff = ib.BundleOffset + 4;
                    if (blen <= 0)
                    {
                        casBadLen++;
                        continue;
                    }

                    byte[] bundleData = new byte[blen];
                    using (FileStream casFs = new FileStream(resolved, FileMode.Open, FileAccess.Read))
                    {
                        casFs.Seek(boff, SeekOrigin.Begin);
                        if (casFs.Read(bundleData, 0, blen) != blen)
                        {
                            casShortRead++;
                            continue;
                        }
                    }

                    DbObject dbObj;
                    using (MemoryStream ms = new MemoryStream(bundleData))
                        dbObj = ReadBundleF21(ms);
                    if (dbObj == null)
                    {
                        casNullObj++;
                        if (casNullObj <= 2)
                        {
                            uint gotMagic = bundleData.Length >= 4
                                ? (uint)((bundleData[0] << 24) | (bundleData[1] << 16) | (bundleData[2] << 8) | bundleData[3])
                                : 0;
                            log?.AppendLine($"    cas NULLOBJ name={ib.Name} cat={ib.CatalogIndex} cas={ib.CasIndex} patch={ib.InPatch} blen={blen} boff={boff} gotMagic=0x{gotMagic:X8} expect=0xD6A03D9D resolved={resolved}");
                        }
                        continue;
                    }

                    AddCasLocations(dbObj, ib.Entries);

                    parent.bundles.Add(new BundleEntry { Name = ib.Name, SuperBundleId = sbIndex });
                    int bid = parent.bundles.Count - 1;
                    parent.ProcessBundleEbx(dbObj, bid, helper);
                    parent.ProcessBundleRes(dbObj, bid, helper);
                    parent.ProcessBundleChunks(dbObj, bid, helper);
                    casOk++;
                }
                log?.AppendLine($"    SUMMARY bundles={bundles.Count} casBundles={casBundles.Count} tocChunks={tocChunks.Count} casOk={casOk} noResolve={casNoResolve} badLen={casBadLen} shortRead={casShortRead} nullObj={casNullObj}");

                // ----------------------------------------------------------------
                // TOC-level chunks
                // ----------------------------------------------------------------
                foreach (TocChunkMeta c in tocChunks)
                {
                    string casPath = parent.fs.GetFilePath(c.CatalogIndex, c.CasIndex, c.InPatch);

                    if (!parent.chunkList.ContainsKey(c.Id))
                        parent.chunkList.Add(c.Id, new ChunkAssetEntry());

                    ChunkAssetEntry entry = parent.chunkList[c.Id];
                    entry.Id       = c.Id;
                    entry.Size     = c.DataSize;
                    entry.Location = AssetDataLocation.CasNonIndexed;
                    entry.ExtraData = new AssetExtraData
                    {
                        DataOffset = c.DataOffset,
                        CasPath    = casPath
                    };
                    entry.IsTocChunk = true;
                }
            }

            // -----------------------------------------------------------------------
            // BundleReader_F21: reads bundle meta (magic 0xD6A03D9D) → DbObject
            // Stream position is at the start of the bundle header.
            // -----------------------------------------------------------------------

            private DbObject ReadBundleF21(Stream stream)
            {
                NativeReader r = new NativeReader(stream);
                long bundleStart = r.Position;

                uint magic = r.ReadUInt(Endian.Big);
                if (magic != BundleMagicF21)
                    return null;

                int  totalCount     = r.ReadInt(Endian.Little);
                int  ebxCount       = r.ReadInt(Endian.Little);
                int  resCount       = r.ReadInt(Endian.Little);
                int  chunkCount     = r.ReadInt(Endian.Little);
                long stringBlockOff = r.ReadInt(Endian.Little) + bundleStart;
                /*chunkMetaOff*/    r.ReadInt(Endian.Little);
                r.ReadInt(Endian.Little); // padding

                // SHA1 hashes
                Sha1[] sha1s = new Sha1[totalCount];
                for (int i = 0; i < totalCount; i++)
                    sha1s[i] = r.ReadSha1();

                // EBX name offsets + original sizes
                uint[] ebxNameOff = new uint[ebxCount];
                uint[] ebxOrigSz  = new uint[ebxCount];
                for (int i = 0; i < ebxCount; i++)
                {
                    ebxNameOff[i] = r.ReadUInt(Endian.Little);
                    ebxOrigSz[i]  = r.ReadUInt(Endian.Little);
                }

                // RES name offsets + original sizes
                uint[] resNameOff = new uint[resCount];
                uint[] resOrigSz  = new uint[resCount];
                for (int i = 0; i < resCount; i++)
                {
                    resNameOff[i] = r.ReadUInt(Endian.Little);
                    resOrigSz[i]  = r.ReadUInt(Endian.Little);
                }

                // Chunk guid + logical offsets/sizes
                Guid[] chunkIds  = new Guid[chunkCount];
                uint[] logOffs   = new uint[chunkCount];
                uint[] logSizes  = new uint[chunkCount];
                for (int i = 0; i < chunkCount; i++)
                {
                    chunkIds[i] = r.ReadGuid(Endian.Little);
                    logOffs[i]  = r.ReadUInt(Endian.Little);
                    logSizes[i] = r.ReadUInt(Endian.Little);
                }

                // RES extra: resType[] | resMeta[] | resRid[]
                uint[]   resTypes = new uint[resCount];
                byte[][] resMetas = new byte[resCount][];
                long[]   resRids  = new long[resCount];
                for (int i = 0; i < resCount; i++) resTypes[i] = r.ReadUInt(Endian.Little);
                for (int i = 0; i < resCount; i++) resMetas[i] = r.ReadBytes(16);
                for (int i = 0; i < resCount; i++) resRids[i]  = r.ReadLong(Endian.Little);

                // Build EBX list
                DbObject ebxList = DbObject.CreateList();
                for (int i = 0; i < ebxCount; i++)
                {
                    long saved = r.Position;
                    r.Position = stringBlockOff + ebxNameOff[i];
                    string name = r.ReadNullTerminatedString();
                    r.Position = saved;

                    DbObject e = DbObject.CreateObject();
                    e.AddValue("sha1",         sha1s[i]);
                    e.AddValue("name",         name);
                    e.AddValue("originalSize", (long)ebxOrigSz[i]);
                    ebxList.Add(e);
                }

                // Build RES list
                DbObject resList = DbObject.CreateList();
                for (int i = 0; i < resCount; i++)
                {
                    long saved = r.Position;
                    r.Position = stringBlockOff + resNameOff[i];
                    string name = r.ReadNullTerminatedString();
                    r.Position = saved;

                    DbObject e = DbObject.CreateObject();
                    e.AddValue("sha1",         sha1s[ebxCount + i]);
                    e.AddValue("name",         name);
                    e.AddValue("originalSize", (long)resOrigSz[i]);
                    e.AddValue("resType",      (long)resTypes[i]);
                    e.AddValue("resMeta",      resMetas[i]);
                    e.AddValue("resRid",       resRids[i]);
                    resList.Add(e);
                }

                // Build Chunks list
                DbObject chunkList = DbObject.CreateList();
                for (int i = 0; i < chunkCount; i++)
                {
                    DbObject e = DbObject.CreateObject();
                    e.AddValue("id",           chunkIds[i]);
                    e.AddValue("sha1",         sha1s[ebxCount + resCount + i]);
                    e.AddValue("logicalOffset",(long)logOffs[i]);
                    e.AddValue("logicalSize",  (long)logSizes[i]);
                    e.AddValue("originalSize", (long)((logOffs[i] & 0xFFFF) | logSizes[i]));
                    chunkList.Add(e);
                }

                DbObject result = DbObject.CreateObject();
                result.AddValue("ebx",    ebxList);
                result.AddValue("res",    resList);
                result.AddValue("chunks", chunkList);
                return result;
            }

            // -----------------------------------------------------------------------
            // SuperBundle (.sb file) reader – equivalent of SuperBundleReader_F21.
            // Reads header + BundleF21 bundle meta + CAS location table.
            // -----------------------------------------------------------------------

            private DbObject ReadSbBundle(MemoryStream ms)
            {
                NativeReader r = new NativeReader(ms);

                int magic = r.ReadInt(Endian.Big);
                if (magic != SuperBundleMagic)
                    return null;

                r.ReadInt(Endian.Big);            // unk
                int flagsOff   = r.ReadInt(Endian.Big);
                int count      = r.ReadInt(Endian.Big);
                int entriesOff = r.ReadInt(Endian.Big);
                r.ReadInt(Endian.Big);
                r.ReadInt(Endian.Big);
                r.ReadInt(Endian.Big);
                r.ReadInt(Endian.Big);

                DbObject dbObj = ReadBundleF21(ms);
                if (dbObj == null)
                    return null;

                List<DbObject> all = new List<DbObject>();
                foreach (object o in dbObj.GetValue<DbObject>("ebx"))    all.Add((DbObject)o);
                foreach (object o in dbObj.GetValue<DbObject>("res"))    all.Add((DbObject)o);
                foreach (object o in dbObj.GetValue<DbObject>("chunks")) all.Add((DbObject)o);

                r.Position = flagsOff;
                byte[] flags = r.ReadBytes(count);

                r.Position = entriesOff;
                int  pkgIdx  = 0;
                int  casIdx  = 0;
                bool isPatch = false;

                for (int j = 0; j < count && j < all.Count; j++)
                {
                    if (flags[j] == 1) // new CAS identifier
                    {
                        int id = r.ReadInt(Endian.Big);
                        pkgIdx  = (id >> 8) & 0xFF;
                        casIdx  = id & 0xFF;
                        isPatch = (id >> 16) != 0;
                    }
                    uint entOff  = r.ReadUInt(Endian.Big);
                    int  entSize = r.ReadInt(Endian.Big);

                    all[j].AddValue("offset",  (long)entOff);
                    all[j].AddValue("size",    (long)entSize);
                    all[j].AddValue("catalog", pkgIdx);
                    all[j].AddValue("cas",     casIdx);
                    if (isPatch)
                        all[j].AddValue("patch", true);
                }

                return dbObj;
            }

            // -----------------------------------------------------------------------
            // Adds CAS location data from entries list to each asset in the DbObject.
            // entries[i] corresponds to ebx[0], ebx[1], ..., res[0], ..., chunk[0], ...
            // -----------------------------------------------------------------------

            private void AddCasLocations(DbObject dbObj, List<CasEntryMeta> entries)
            {
                List<DbObject> all = new List<DbObject>();
                foreach (object o in dbObj.GetValue<DbObject>("ebx"))    all.Add((DbObject)o);
                foreach (object o in dbObj.GetValue<DbObject>("res"))    all.Add((DbObject)o);
                foreach (object o in dbObj.GetValue<DbObject>("chunks")) all.Add((DbObject)o);

                int count = Math.Min(all.Count, entries.Count);
                for (int i = 0; i < count; i++)
                {
                    CasEntryMeta e = entries[i];
                    all[i].AddValue("offset",  (long)e.EntryOffset);
                    all[i].AddValue("size",    (long)e.EntrySize);
                    all[i].AddValue("catalog", e.CatalogIndex);
                    all[i].AddValue("cas",     e.CasIndex);
                    if (e.InPatch)
                        all[i].AddValue("patch", true);
                }
            }

            // -----------------------------------------------------------------------
            // Helper: add or find superbundle in parent list
            // -----------------------------------------------------------------------

            private int GetOrAddSuperBundle(AssetManager parent, string name)
            {
                SuperBundleEntry existing = parent.superBundles.Find(s => s.Name == name);
                if (existing != null)
                    return parent.superBundles.IndexOf(existing);
                parent.superBundles.Add(new SuperBundleEntry { Name = name });
                return parent.superBundles.Count - 1;
            }
        }
    }
}
