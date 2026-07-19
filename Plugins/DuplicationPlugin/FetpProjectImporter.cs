using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.IO;

namespace DuplicationPlugin
{
    // Applies a parsed FETP (FC 24/25/26 FIFA Editor Tool) project to the asset manager.
    internal static class FetpProjectImporter
    {
        public class ImportStats
        {
            public int BundlesAdded;
            public int ChunksApplied;
            public int ResApplied;
            public int EbxApplied;
            public int Skipped;
            public int Failed;
        }

        public static ImportStats Apply(FetpProjectReader.FetpResult result, Action<string> progress)
        {
            ImportStats stats = new ImportStats();

            // 1. Bundles — created by name; ids kept for asset assignment below.
            List<KeyValuePair<string, int>> addedBundles = new List<KeyValuePair<string, int>>();
            foreach (FetpProjectReader.FetpBundle b in result.Bundles)
            {
                App.AssetManager.AddBundle(b.Name, (BundleType)b.Type, 0);
                int bid = App.AssetManager.GetBundleId(b.Name);
                if (bid != -1)
                    addedBundles.Add(new KeyValuePair<string, int>(b.Name, bid));
                stats.BundlesAdded++;
            }

            // 2. Chunks — FET stores CAS-compressed data, same representation Frosty
            // keeps in ModifiedEntry.Data, so blobs transplant without recompression.
            Dictionary<Guid, ChunkAssetEntry> importedChunks = new Dictionary<Guid, ChunkAssetEntry>();
            int current = 0;
            foreach (FetpProjectReader.FetpChunk chunk in result.Chunks)
            {
                current++;
                progress(string.Format("Applying chunk {0}/{1}", current, result.Chunks.Count));
                try
                {
                    ChunkAssetEntry entry = App.AssetManager.GetChunkEntry(chunk.Id);
                    if (entry == null)
                    {
                        if (!chunk.IsAdded)
                        {
                            App.Logger.Log("  Modified chunk {0} not found in game, adding it instead", chunk.Id);
                        }
                        entry = new ChunkAssetEntry { Id = chunk.Id, H32 = unchecked((int)chunk.H32) };
                        App.AssetManager.AddChunk(entry);
                    }

                    entry.ModifiedEntry = new ModifiedAssetEntry
                    {
                        Sha1 = new Sha1(chunk.Sha1),
                        Data = chunk.Data,
                        LogicalOffset = chunk.LogicalOffset,
                        LogicalSize = chunk.LogicalSize,
                        H32 = unchecked((int)chunk.H32),
                        FirstMip = -1,
                        IsDirty = true
                    };
                    entry.IsDirty = true;
                    importedChunks[chunk.Id] = entry;
                    stats.ChunksApplied++;
                }
                catch (Exception ex)
                {
                    App.Logger.Log("  Chunk {0} failed: {1}", chunk.Id, ex.Message);
                    stats.Failed++;
                }
            }

            // 3. Res
            Dictionary<string, ResAssetEntry> importedRes = new Dictionary<string, ResAssetEntry>(StringComparer.OrdinalIgnoreCase);
            current = 0;
            foreach (FetpProjectReader.FetpRes res in result.Res)
            {
                current++;
                progress(string.Format("Applying res {0}/{1}", current, result.Res.Count));
                if (!res.IsDirectlyModified)
                    continue;
                try
                {
                    ResAssetEntry entry = App.AssetManager.GetResEntry(res.Name);
                    if (entry == null)
                    {
                        if (!res.IsAdded)
                        {
                            App.Logger.Log("  Modified res {0} not found in game, adding it instead", res.Name);
                        }
                        entry = new ResAssetEntry
                        {
                            Name = res.Name,
                            ResType = res.ResType,
                            ResRid = res.ResRid,
                            ResMeta = res.Meta
                        };
                        App.AssetManager.AddRes(entry);
                    }

                    entry.ModifiedEntry = new ModifiedAssetEntry
                    {
                        Sha1 = new Sha1(res.Sha1),
                        Data = res.Data,
                        OriginalSize = res.OriginalSize,
                        ResMeta = res.Meta,
                        IsDirty = true
                    };
                    entry.IsDirty = true;
                    importedRes[res.Name] = entry;
                    stats.ResApplied++;
                }
                catch (Exception ex)
                {
                    App.Logger.Log("  Res {0} failed: {1}", res.Name, ex.Message);
                    stats.Failed++;
                }
            }

            // 4. EBX — decompress the CAS blob, parse with the (FC 26-capable) EbxReader,
            // then hand the parsed asset to the manager like a normal edit.
            current = 0;
            foreach (FetpProjectReader.FetpEbx ebx in result.Ebx)
            {
                current++;
                progress(string.Format("Applying EBX {0}/{1}: {2}", current, result.Ebx.Count, ebx.Name));
                if (!ebx.IsDirectlyModified)
                    continue;
                try
                {
                    EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebx.Name);
                    if (entry == null)
                    {
                        if (!ebx.IsAdded)
                        {
                            App.Logger.Log("  Modified EBX {0} not found in game, registering it as added", ebx.Name);
                        }
                        entry = new EbxAssetEntry
                        {
                            Name = ebx.Name,
                            Guid = ebx.Guid != Guid.Empty ? ebx.Guid : Guid.NewGuid(),
                            Type = ebx.Type
                        };
                        App.AssetManager.AddEbx(entry);
                    }

                    byte[] rawEbx;
                    using (CasReader casReader = new CasReader(new MemoryStream(ebx.Data)))
                        rawEbx = casReader.Read();

                    using (EbxReader reader = EbxReader.CreateReader(new MemoryStream(rawEbx), App.FileSystem, true))
                    {
                        EbxAsset asset = reader.ReadAsset<EbxAsset>();
                        App.AssetManager.ModifyEbx(ebx.Name, asset);
                        if (entry.IsAdded && string.IsNullOrEmpty(entry.Type))
                            entry.Type = asset.RootObject.GetType().Name;
                    }

                    // Bundle assignment: pick the added bundle sharing the longest
                    // path prefix with this asset, then propagate to linked res/chunks.
                    int bundleId = FindBestBundle(addedBundles, ebx.Name);
                    if (bundleId != -1)
                    {
                        if (!entry.AddedBundles.Contains(bundleId))
                            entry.AddedBundles.Add(bundleId);
                        foreach (FetpProjectReader.FetpLinkedAsset la in ebx.LinkedAssets)
                        {
                            AssetEntry linked = null;
                            if (la.AssetType == 1 && la.Name != null && importedRes.ContainsKey(la.Name))
                                linked = importedRes[la.Name];
                            else if (la.AssetType == 2 && importedChunks.ContainsKey(la.ChunkId))
                                linked = importedChunks[la.ChunkId];
                            if (linked != null && !linked.AddedBundles.Contains(bundleId))
                                linked.AddedBundles.Add(bundleId);
                        }
                    }

                    // Restore linked-asset relationships for the editor.
                    foreach (FetpProjectReader.FetpLinkedAsset la in ebx.LinkedAssets)
                    {
                        AssetEntry linked = null;
                        if (la.AssetType == 0 && la.Name != null)
                            linked = App.AssetManager.GetEbxEntry(la.Name);
                        else if (la.AssetType == 1 && la.Name != null)
                            linked = App.AssetManager.GetResEntry(la.Name);
                        else if (la.AssetType == 2)
                            linked = App.AssetManager.GetChunkEntry(la.ChunkId);
                        if (linked != null && !entry.LinkedAssets.Contains(linked))
                            entry.LinkedAssets.Add(linked);
                    }

                    stats.EbxApplied++;
                }
                catch (Exception ex)
                {
                    App.Logger.Log("  EBX {0} failed: {1}", ebx.Name, ex.Message);
                    stats.Failed++;
                }
            }

            return stats;
        }

        private static int FindBestBundle(List<KeyValuePair<string, int>> bundles, string assetName)
        {
            int bestId = -1;
            int bestLen = -1;
            foreach (KeyValuePair<string, int> kv in bundles)
            {
                int common = CommonPrefixLength(kv.Key, assetName);
                if (common > bestLen)
                {
                    bestLen = common;
                    bestId = kv.Value;
                }
            }
            return bestId;
        }

        private static int CommonPrefixLength(string a, string b)
        {
            int max = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < max && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
                i++;
            return i;
        }
    }
}
