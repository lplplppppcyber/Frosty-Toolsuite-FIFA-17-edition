using BundleRefTablePlugin;
using DuplicationPlugin.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Viewport;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Media;

namespace DuplicationPlugin
{
    public class DuplicateFolderMenuExtension : MenuExtension
    {
        private readonly Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions
            = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

        public DuplicateFolderMenuExtension()
        {
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.IsSubclassOf(typeof(DuplicationTool.DuplicateAssetExtension)))
                {
                    var ext = (DuplicationTool.DuplicateAssetExtension)Activator.CreateInstance(type);
                    if (ext.AssetType != null)
                        extensions[ext.AssetType] = ext;
                }
            }
            extensions["null"] = new DuplicationTool.DuplicateAssetExtension();
        }

        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => null;
        public override string MenuItemName => "Duplicate Folder";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            if (entry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select any asset inside the folder you want to duplicate.",
                    "Folder Duplicator");
                return;
            }

            string sourceFolder = entry.Path.Replace('\\', '/');
            if (string.IsNullOrEmpty(sourceFolder))
            {
                FrostyMessageBox.Show("Selected asset has no folder path.", "Folder Duplicator");
                return;
            }

            // Strip BRT subfolder suffix if user selected an asset from inside a BRT folder.
            // e.g. "content/.../ball_999_ball_brt" -> "content/.../ball_999"
            //      "content/.../ball_999_launch_ball_brt" -> "content/.../ball_999"
            int slashIdx = sourceFolder.LastIndexOf('/');
            string folderName = slashIdx >= 0 ? sourceFolder.Substring(slashIdx + 1) : sourceFolder;
            string folderParent = slashIdx >= 0 ? sourceFolder.Substring(0, slashIdx) : "";

            if (folderName.EndsWith("_brt", StringComparison.OrdinalIgnoreCase))
            {
                string withoutBrt = folderName.Substring(0, folderName.Length - 4);
                int lastUnderscore = withoutBrt.LastIndexOf('_');
                if (lastUnderscore > 0)
                {
                    string candidate = withoutBrt.Substring(0, lastUnderscore);
                    if (candidate.EndsWith("_launch", StringComparison.OrdinalIgnoreCase))
                        candidate = candidate.Substring(0, candidate.Length - 7);
                    sourceFolder = (folderParent.Length > 0 ? folderParent + "/" : "") + candidate;
                }
            }

            DuplicateFolderWindow win = new DuplicateFolderWindow(sourceFolder);
            if (win.ShowDialog() != true)
                return;

            string newFolderName = win.NewFolderName;
            string destPath = win.DestinationPath;

            FrostyTaskWindow.Show("Duplicating Folder", "", (task) =>
            {
                try
                {
                    if (!MeshVariationDb.IsLoaded)
                        MeshVariationDb.LoadVariations(task);

                    DuplicateFolder(task, sourceFolder, newFolderName, destPath);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating folder: " + ex.ToString());
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        });

        public static string ExtractId(string folderName)
        {
            int last = folderName.LastIndexOf('_');
            if (last < 0) return null;
            string candidate = folderName.Substring(last + 1);
            int dummy;
            return int.TryParse(candidate, out dummy) ? candidate : null;
        }

        private EbxAssetEntry DuplicateWithExtension(EbxAssetEntry entry, string newName)
        {
            try
            {
                string key = "null";
                foreach (string typekey in extensions.Keys)
                {
                    if (typekey != "null" && TypeLibrary.IsSubClassOf(entry.Type, typekey))
                    {
                        key = typekey;
                        break;
                    }
                }
                return extensions[key].DuplicateAsset(entry, newName, false, null);
            }
            catch (Exception ex)
            {
                App.Logger.Log("Failed to duplicate " + entry.Name + ": " + ex.ToString());
                return null;
            }
        }

        private void DuplicateFolder(FrostyTaskWindow task, string sourceFolder,
            string newFolderName, string destPath)
        {
            int srcSlash = sourceFolder.LastIndexOf('/');
            string sourceFolderName = srcSlash >= 0 ? sourceFolder.Substring(srcSlash + 1) : sourceFolder;
            string sourceParent = srcSlash >= 0 ? sourceFolder.Substring(0, srcSlash) : "";
            string newFolder = destPath.TrimEnd('/') + "/" + newFolderName;

            string oldId = ExtractId(sourceFolderName);
            string newId = ExtractId(newFolderName);
            bool hasIdReplacement = !string.IsNullOrEmpty(oldId)
                && !string.IsNullOrEmpty(newId)
                && oldId != newId;

            App.Logger.Log("Folder source: " + sourceFolder);
            App.Logger.Log("Folder target: " + newFolder);
            if (hasIdReplacement)
                App.Logger.Log("  ID replacement: " + oldId + " -> " + newId);

            // ── Phase 1: Enumerate ──────────────────────────────────────────────
            task.Update("Finding assets...");

            List<EbxAssetEntry> mainAssets = new List<EbxAssetEntry>();
            // BRT sibling folders: same parent dir, name starts with sourceFolderName + "_", ends with "_brt"
            Dictionary<string, List<EbxAssetEntry>> brtFolderMap =
                new Dictionary<string, List<EbxAssetEntry>>(StringComparer.OrdinalIgnoreCase);

            string sourceFolderNameLower = sourceFolderName.ToLowerInvariant();

            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                string path = e.Path.Replace('\\', '/');

                if (path.Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
                {
                    mainAssets.Add(e);
                    continue;
                }

                // Check for sibling BRT folders: same parent, name = sourceFolderName + "_*_brt"
                int pathSlash = path.LastIndexOf('/');
                string pathParent = pathSlash >= 0 ? path.Substring(0, pathSlash) : "";
                if (!pathParent.Equals(sourceParent, StringComparison.OrdinalIgnoreCase))
                    continue;

                string siblingName = (pathSlash >= 0 ? path.Substring(pathSlash + 1) : path)
                    .ToLowerInvariant();

                if (siblingName.StartsWith(sourceFolderNameLower + "_") && siblingName.EndsWith("_brt"))
                {
                    if (!brtFolderMap.ContainsKey(path))
                        brtFolderMap[path] = new List<EbxAssetEntry>();
                    brtFolderMap[path].Add(e);
                }
            }

            int brtTotal = brtFolderMap.Values.Sum(l => l.Count);
            App.Logger.Log("Found " + mainAssets.Count + " main assets, "
                + brtTotal + " BRT assets in " + brtFolderMap.Count + " BRT folder(s)");

            if (mainAssets.Count == 0)
            {
                App.Logger.Log("No assets found in: " + sourceFolder);
                return;
            }

            // ── Phase 2: Duplicate ──────────────────────────────────────────────
            Dictionary<Guid, EbxAssetEntry> oldToNew = new Dictionary<Guid, EbxAssetEntry>();
            Dictionary<string, string> oldToNewNames = new Dictionary<string, string>();
            List<EbxAssetEntry> allNew = new List<EbxAssetEntry>();

            int total = mainAssets.Count + brtTotal;
            int current = 0;

            foreach (EbxAssetEntry src in mainAssets)
            {
                current++;
                string newFilename = hasIdReplacement
                    ? src.Filename.Replace(oldId, newId)
                    : src.Filename;
                string newAssetName = newFolder + "/" + newFilename;
                task.Update("Duplicating " + src.Filename + " (" + current + "/" + total + ")...");

                EbxAssetEntry newEntry = DuplicateWithExtension(src, newAssetName);
                if (newEntry != null)
                {
                    oldToNew[src.Guid] = newEntry;
                    oldToNewNames[src.Name] = newEntry.Name;
                    allNew.Add(newEntry);
                    App.Logger.Log("  Duplicated: " + src.Name + " -> " + newEntry.Name);
                }
            }

            foreach (KeyValuePair<string, List<EbxAssetEntry>> kvp in brtFolderMap)
            {
                string srcBrtFolder = kvp.Key;
                int brtSlash = srcBrtFolder.LastIndexOf('/');
                string srcBrtFolderName = brtSlash >= 0
                    ? srcBrtFolder.Substring(brtSlash + 1)
                    : srcBrtFolder;
                // Preserve the suffix after the source folder name (e.g. "_ball_brt")
                string brtSuffix = srcBrtFolderName.Substring(sourceFolderName.Length);
                string newBrtFolder = destPath.TrimEnd('/') + "/" + newFolderName + brtSuffix;

                foreach (EbxAssetEntry src in kvp.Value)
                {
                    current++;
                    string newFilename = hasIdReplacement
                        ? src.Filename.Replace(oldId, newId)
                        : src.Filename;
                    string newAssetName = newBrtFolder + "/" + newFilename;
                    task.Update("Duplicating " + src.Filename + " (" + current + "/" + total + ")...");

                    EbxAssetEntry newEntry = DuplicateWithExtension(src, newAssetName);
                    if (newEntry != null)
                    {
                        oldToNew[src.Guid] = newEntry;
                        oldToNewNames[src.Name] = newEntry.Name;
                        allNew.Add(newEntry);
                        App.Logger.Log("  Duplicated BRT asset: " + src.Name + " -> " + newEntry.Name);
                    }
                }
            }

            // ── Phase 3: Fix references ─────────────────────────────────────────
            task.Update("Fixing cross-references...");
            FixCrossReferences(oldToNew, allNew);

            // ── Phase 4: BRT injection ──────────────────────────────────────────
            if (!Config.Get<bool>("SkipBrtAdd", false))
            {
                task.Update("Updating BRT entries...");
                InjectBrtEntries(mainAssets, oldToNewNames);
            }

            App.Logger.Log("Folder duplication complete (" + allNew.Count + " assets)");
        }

        // ─── BRT Injection ──────────────────────────────────────────────────────

        private void InjectBrtEntries(List<EbxAssetEntry> sourceAssets,
            Dictionary<string, string> oldToNewNames)
        {
            Dictionary<string, string> brtPairs = new Dictionary<string, string>();
            foreach (EbxAssetEntry src in sourceAssets)
            {
                if (oldToNewNames.ContainsKey(src.Name))
                    brtPairs[src.Name.ToLower()] = oldToNewNames[src.Name].ToLower();
            }

            if (brtPairs.Count == 0)
            {
                App.Logger.Log("  No BRT-eligible assets to inject.");
                return;
            }

            App.Logger.Log("  BRT-eligible assets: " + brtPairs.Count);

            List<ResAssetEntry> allBrts = App.AssetManager
                .EnumerateRes((uint)ResourceType.BundleRefTableResource).ToList();
            App.Logger.Log("  Found " + allBrts.Count + " BRT res entries total");

            foreach (ResAssetEntry brtRes in allBrts)
            {
                BundleRefTableResource brt = App.AssetManager.GetResAs<BundleRefTableResource>(brtRes);
                if (brt == null)
                    continue;

                bool brtModified = false;

                foreach (KeyValuePair<string, string> kvp in brtPairs)
                {
                    if (brt.ContainsAsset(kvp.Key))
                    {
                        if (brt.DupeAsset(kvp.Value, kvp.Key))
                        {
                            brtModified = true;
                            App.Logger.Log("  BRT " + brtRes.Filename + ": " + kvp.Value);
                        }
                    }
                }

                if (brtModified)
                {
                    App.AssetManager.ModifyRes(brtRes.ResRid, brt);
                    App.Logger.Log("  Saved BRT: " + brtRes.Name);
                }
            }
        }

        // ─── Cross-Reference Fixup ──────────────────────────────────────────────

        private void FixCrossReferences(Dictionary<Guid, EbxAssetEntry> oldToNew,
            List<EbxAssetEntry> newAssets)
        {
            foreach (EbxAssetEntry newEntry in newAssets)
            {
                try
                {
                    // TextureAsset / pure mesh resources have no cross-refs to remap.
                    if (newEntry.Type == "TextureAsset"
                        || newEntry.Type == "RigidMeshAsset"
                        || newEntry.Type == "SkinnedMeshAsset")
                        continue;

                    EbxAsset asset = App.AssetManager.GetEbx(newEntry);
                    if (EbxRefFixer.Fix(asset, oldToNew))
                    {
                        asset.Update();
                        App.AssetManager.ModifyEbx(newEntry.Name, asset);
                        App.Logger.Log("  Fixed refs: " + newEntry.Name);
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Failed to fix refs in " + newEntry.Name + ": " + ex.Message);
                }
            }
        }
    }
}
