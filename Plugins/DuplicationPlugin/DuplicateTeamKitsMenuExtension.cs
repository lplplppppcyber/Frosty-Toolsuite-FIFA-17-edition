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
    public class DuplicateTeamKitsMenuExtension : MenuExtension
    {
        private readonly Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions
            = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

        public DuplicateTeamKitsMenuExtension()
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
        public override string MenuItemName => "Duplicate Team Kits";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            if (entry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select any asset inside any kit subfolder of the team you want to duplicate.",
                    "Team Kit Duplicator");
                return;
            }

            // Selected asset path: "content/.../alanyaspor_171/home_0_0"
            // We need to go up one level to get the team folder.
            string kitFolder = entry.Path.Replace('\\', '/');
            if (string.IsNullOrEmpty(kitFolder))
            {
                FrostyMessageBox.Show("Selected asset has no folder path.", "Team Kit Duplicator");
                return;
            }

            int lastSlash = kitFolder.LastIndexOf('/');
            if (lastSlash <= 0)
            {
                FrostyMessageBox.Show(
                    "Selected asset is not deep enough to detect a team folder.\n" +
                    "Select an asset inside a kit subfolder (e.g. home_0_0) of the team.",
                    "Team Kit Duplicator");
                return;
            }

            string teamFolder = kitFolder.Substring(0, lastSlash);

            string teamFolderName = teamFolder.Substring(teamFolder.LastIndexOf('/') + 1);
            string oldTeamId = ExtractTrailingId(teamFolderName);
            if (string.IsNullOrEmpty(oldTeamId))
            {
                FrostyMessageBox.Show(
                    "Could not extract a numeric team ID from '" + teamFolderName + "'.\n" +
                    "Expected format: teamname_999",
                    "Team Kit Duplicator");
                return;
            }

            DuplicateTeamKitsWindow win = new DuplicateTeamKitsWindow(teamFolder);
            if (win.ShowDialog() != true)
                return;

            string newTeamFolderName = win.NewTeamFolderName;
            string destPath = win.DestinationPath;

            FrostyTaskWindow.Show("Duplicating Team Kits", "", (task) =>
            {
                try
                {
                    if (!MeshVariationDb.IsLoaded)
                        MeshVariationDb.LoadVariations(task);

                    DuplicateTeam(task, teamFolder, newTeamFolderName, destPath);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating team kits: " + ex.ToString());
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        });

        private static string ExtractTrailingId(string folderName)
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

        private void DuplicateTeam(FrostyTaskWindow task, string teamFolder,
            string newTeamFolderName, string destPath)
        {
            string teamFolderName = teamFolder.Substring(teamFolder.LastIndexOf('/') + 1);
            string newTeamFolder = destPath.TrimEnd('/') + "/" + newTeamFolderName;

            string oldTeamId = ExtractTrailingId(teamFolderName);
            string newTeamId = ExtractTrailingId(newTeamFolderName);

            if (string.IsNullOrEmpty(oldTeamId) || string.IsNullOrEmpty(newTeamId))
            {
                App.Logger.Log("Could not extract team IDs. Aborting.");
                return;
            }

            string oldPattern = "_" + oldTeamId + "_";
            string newPattern = "_" + newTeamId + "_";
            string oldEnd = "_" + oldTeamId;
            string newEnd = "_" + newTeamId;

            App.Logger.Log("Team source: " + teamFolder + " (ID " + oldTeamId + ")");
            App.Logger.Log("Team target: " + newTeamFolder + " (ID " + newTeamId + ")");

            // ── Phase 1: Enumerate all assets under the team folder ─────────────
            task.Update("Finding team assets...");

            // Assets whose path starts with teamFolder + "/" belong to this team.
            // This covers all kit subfolders (home_0_0, away_1_0, third_2_0, etc.).
            string teamPrefix = teamFolder + "/";

            List<EbxAssetEntry> allAssets = new List<EbxAssetEntry>();

            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                string path = e.Path.Replace('\\', '/');
                if (path.StartsWith(teamPrefix, StringComparison.OrdinalIgnoreCase))
                    allAssets.Add(e);
            }

            App.Logger.Log("Found " + allAssets.Count + " assets under team folder");

            if (allAssets.Count == 0)
            {
                App.Logger.Log("No assets found under: " + teamFolder);
                return;
            }

            // ── Phase 2: Duplicate ──────────────────────────────────────────────
            Dictionary<Guid, EbxAssetEntry> oldToNew = new Dictionary<Guid, EbxAssetEntry>();
            Dictionary<string, string> oldToNewNames = new Dictionary<string, string>();
            List<EbxAssetEntry> allNew = new List<EbxAssetEntry>();

            int current = 0;
            int total = allAssets.Count;

            foreach (EbxAssetEntry src in allAssets)
            {
                current++;
                task.Update("Duplicating " + src.Filename + " (" + current + "/" + total + ")...");

                // Compute the relative sub-path within the team folder
                // e.g. "home_0_0/jersey_171_0_0_color"
                string srcPath = src.Path.Replace('\\', '/');
                string relPath = srcPath.Substring(teamPrefix.Length); // e.g. "home_0_0"

                // Rename the filename: replace team ID occurrences surrounded by underscores
                string newFilename = src.Filename;
                if (oldPattern != newPattern)
                {
                    newFilename = newFilename.Replace(oldPattern, newPattern);
                    if (newFilename.EndsWith(oldEnd))
                        newFilename = newFilename.Substring(0, newFilename.Length - oldEnd.Length) + newEnd;
                }

                // Kit subfolder names don't embed the team ID (home_0_0 stays home_0_0)
                // so relPath needs no renaming.
                string newAssetName = newTeamFolder + "/" + relPath + "/" + newFilename;

                EbxAssetEntry newEntry = DuplicateWithExtension(src, newAssetName);
                if (newEntry != null)
                {
                    oldToNew[src.Guid] = newEntry;
                    oldToNewNames[src.Name] = newEntry.Name;
                    allNew.Add(newEntry);
                    App.Logger.Log("  Duplicated: " + src.Name + " -> " + newEntry.Name);
                }
            }

            // ── Phase 3: Fix references ─────────────────────────────────────────
            task.Update("Fixing cross-references...");
            FixCrossReferences(oldToNew, allNew);

            // ── Phase 4: BRT injection ──────────────────────────────────────────
            if (!Config.Get<bool>("SkipBrtAdd", false))
            {
                task.Update("Updating BRT entries...");
                InjectBrtEntries(allAssets, oldToNewNames);
            }

            App.Logger.Log("Team kit duplication complete (" + allNew.Count + " assets)");
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
