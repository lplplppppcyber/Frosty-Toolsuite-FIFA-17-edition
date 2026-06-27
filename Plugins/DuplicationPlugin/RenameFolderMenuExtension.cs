using DuplicationPlugin.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Media;

namespace DuplicationPlugin
{
    public class RenameFolderMenuExtension : MenuExtension
    {
        private readonly Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions
            = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

        public RenameFolderMenuExtension()
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
        public override string MenuItemName => "Rename Folder";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            if (entry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select any asset inside the folder you want to rename.",
                    "Folder Renamer");
                return;
            }

            string sourceFolder = entry.Path.Replace('\\', '/');
            if (string.IsNullOrEmpty(sourceFolder))
            {
                FrostyMessageBox.Show("Selected asset has no folder path.", "Folder Renamer");
                return;
            }

            RenameFolderWindow win = new RenameFolderWindow(sourceFolder);
            if (win.ShowDialog() != true)
                return;

            string newFolderName = win.NewFolderName;
            string destPath = win.DestinationPath;

            FrostyTaskWindow.Show("Renaming Folder", "", (task) =>
            {
                try
                {
                    RenameFolder(task, sourceFolder, newFolderName, destPath);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error renaming folder: " + ex.ToString());
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        });

        private EbxAssetEntry DuplicateWithExtension(EbxAssetEntry src, string newName)
        {
            try
            {
                string key = "null";
                foreach (string typekey in extensions.Keys)
                {
                    if (typekey != "null" && TypeLibrary.IsSubClassOf(src.Type, typekey))
                    {
                        key = typekey;
                        break;
                    }
                }
                return extensions[key].DuplicateAsset(src, newName, false, null);
            }
            catch (Exception ex)
            {
                App.Logger.Log("Failed to duplicate " + src.Name + ": " + ex.ToString());
                return null;
            }
        }

        private void RenameFolder(FrostyTaskWindow task, string sourceFolder,
            string newFolderName, string destPath)
        {
            int srcSlash = sourceFolder.LastIndexOf('/');
            string sourceFolderName = srcSlash >= 0 ? sourceFolder.Substring(srcSlash + 1) : sourceFolder;
            string newFolder = destPath.TrimEnd('/') + "/" + newFolderName;

            string oldId = DuplicateFolderMenuExtension.ExtractId(sourceFolderName);
            string newId = DuplicateFolderMenuExtension.ExtractId(newFolderName);
            bool hasIdReplacement = !string.IsNullOrEmpty(oldId)
                && !string.IsNullOrEmpty(newId)
                && oldId != newId;

            App.Logger.Log("Rename source: " + sourceFolder);
            App.Logger.Log("Rename target: " + newFolder);
            if (hasIdReplacement)
                App.Logger.Log("  ID replacement: " + oldId + " -> " + newId);

            // ── Phase 1: Enumerate ──────────────────────────────────────────────
            task.Update("Finding assets...");

            List<EbxAssetEntry> sourceAssets = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                string path = e.Path.Replace('\\', '/');
                if (path.Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
                    sourceAssets.Add(e);
            }

            App.Logger.Log("Found " + sourceAssets.Count + " assets in folder");

            if (sourceAssets.Count == 0)
            {
                App.Logger.Log("No assets found in: " + sourceFolder);
                return;
            }

            // ── Phase 2: Duplicate to new path ─────────────────────────────────
            Dictionary<Guid, EbxAssetEntry> oldToNew = new Dictionary<Guid, EbxAssetEntry>();
            List<EbxAssetEntry> allNew = new List<EbxAssetEntry>();
            List<EbxAssetEntry> toRevert = new List<EbxAssetEntry>();

            int total = sourceAssets.Count;
            int current = 0;

            foreach (EbxAssetEntry src in sourceAssets)
            {
                current++;
                task.Update("Duplicating " + src.Filename + " (" + current + "/" + total + ")...");

                string newFilename = hasIdReplacement
                    ? src.Filename.Replace(oldId, newId)
                    : src.Filename;
                string newAssetName = newFolder + "/" + newFilename;

                EbxAssetEntry newEntry = DuplicateWithExtension(src, newAssetName);
                if (newEntry != null)
                {
                    oldToNew[src.Guid] = newEntry;
                    allNew.Add(newEntry);
                    App.Logger.Log("  Duplicated: " + src.Name + " -> " + newEntry.Name);

                    if (src.IsAdded)
                        toRevert.Add(src);
                    else
                        App.Logger.Log("  Note: " + src.Name + " is a base asset and cannot be removed.");
                }
            }

            // ── Phase 3: Fix cross-references in new assets ─────────────────────
            task.Update("Fixing cross-references...");
            foreach (EbxAssetEntry newEntry in allNew)
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

            // ── Phase 4: Remove old added assets ────────────────────────────────
            task.Update("Removing old assets...");
            foreach (EbxAssetEntry old in toRevert)
            {
                try
                {
                    App.AssetManager.RevertAsset(old);
                    App.Logger.Log("  Removed: " + old.Name);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Failed to remove " + old.Name + ": " + ex.Message);
                }
            }

            int skipped = sourceAssets.Count - toRevert.Count;
            App.Logger.Log("Rename complete: " + allNew.Count + " duplicated, "
                + toRevert.Count + " old assets removed"
                + (skipped > 0 ? ", " + skipped + " base assets kept" : ""));
        }
    }
}
