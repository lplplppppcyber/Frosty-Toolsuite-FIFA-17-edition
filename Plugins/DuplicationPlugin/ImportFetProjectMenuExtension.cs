using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Controls;
using Frosty.Core.Windows;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.IO;
using System.Windows.Media;

namespace DuplicationPlugin
{
    public class ImportFetProjectMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => null;
        public override string MenuItemName => "Import FET Project";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            FrostyOpenFileDialog dlg = new FrostyOpenFileDialog(
                "Open FIFA Editor Tool Project",
                "FET Project Files (*.fifaproject)|*.fifaproject",
                "FetProject");
            if (dlg.ShowDialog() != true)
                return;

            string filename = dlg.FileName;
            string importError = null;
            string summary = null;

            bool isFetp;
            try
            {
                isFetp = FetpProjectReader.IsFetpFile(filename);
            }
            catch (Exception ex)
            {
                FrostyMessageBox.Show("Failed to open file:\n\n" + ex.Message, "Import Error");
                return;
            }

            FrostyTaskWindow.Show("Importing FET Project", "", (task) =>
            {
                try
                {
                    if (isFetp)
                    {
                        // Newer "FETP" format (FC 24/25/26)
                        task.Update("Reading FETP project file...");
                        FetpProjectReader.FetpResult result = FetpProjectReader.Read(filename);

                        App.Logger.Log("FETP project: version={0}, tool={1}, game={2}, gameVersion={3}",
                            result.ProjectVersion, result.ToolVersion, result.GameName, result.GameVersion);
                        App.Logger.Log("  Bundles: {0}, Chunks: {1}, Res: {2}, EBX: {3}",
                            result.Bundles.Count, result.Chunks.Count, result.Res.Count, result.Ebx.Count);

                        FetpProjectImporter.ImportStats stats =
                            FetpProjectImporter.Apply(result, (msg) => task.Update(msg));

                        App.Logger.Log(
                            "FETP import complete: {0} bundles, {1} chunks, {2} res, {3} EBX applied, {4} failed",
                            stats.BundlesAdded, stats.ChunksApplied, stats.ResApplied, stats.EbxApplied, stats.Failed);

                        summary = string.Format(
                            "FET project imported ({0}).\n\nBundles: {1}\nChunks: {2}\nRes: {3}\nEBX: {4}\nFailed: {5}",
                            result.GameName, stats.BundlesAdded, stats.ChunksApplied,
                            stats.ResApplied, stats.EbxApplied, stats.Failed);
                    }
                    else
                    {
                        // Classic "FIFATOOL" format (FIFA 17 - FIFA 23)
                        task.Update("Reading FET project file...");
                        FetProjectReader.FetReadResult result = FetProjectReader.Read(filename);

                        App.Logger.Log("FET project: version={0}, game={1}", result.ProjectVersion, result.GameName);
                        App.Logger.Log("  Added EBX: {0}, Modified EBX: {1}", result.AddedEbx.Count, result.ModifiedEbx.Count);

                        int applied = 0, added = 0, skipped = 0, failed = 0;

                        task.Update("Registering added EBX entries...");
                        foreach (var ae in result.AddedEbx)
                        {
                            if (App.AssetManager.GetEbxEntry(ae.Name) == null)
                            {
                                EbxAssetEntry entry = new EbxAssetEntry
                                {
                                    Name = ae.Name,
                                    Guid = ae.Guid
                                };
                                App.AssetManager.AddEbx(entry);
                                added++;
                                App.Logger.Log("  Registered added EBX: " + ae.Name);
                            }
                        }

                        int total = result.ModifiedEbx.Count;
                        int current = 0;
                        foreach (var me in result.ModifiedEbx)
                        {
                            current++;
                            task.Update("Applying EBX " + current + "/" + total + ": " + me.Name);
                            try
                            {
                                EbxAssetEntry entry = App.AssetManager.GetEbxEntry(me.Name);
                                if (entry == null)
                                {
                                    App.Logger.Log("  Skipped (not found in game): " + me.Name);
                                    skipped++;
                                    continue;
                                }

                                using (EbxReader reader = EbxReader.CreateReader(new MemoryStream(me.Data)))
                                {
                                    EbxAsset asset = reader.ReadAsset<EbxAsset>();
                                    App.AssetManager.ModifyEbx(me.Name, asset);
                                    applied++;
                                }
                            }
                            catch (Exception ex)
                            {
                                App.Logger.Log("  Failed to apply " + me.Name + ": " + ex.Message);
                                failed++;
                            }
                        }

                        App.Logger.Log(
                            "FET import complete: {0} applied, {1} added registrations, {2} skipped, {3} failed",
                            applied, added, skipped, failed);

                        summary = string.Format(
                            "FET project imported.\n\nApplied: {0} EBX\nAdded: {1} new entries\nSkipped: {2} (not in game)\nFailed: {3}",
                            applied, added, skipped, failed);
                    }
                }
                catch (Exception ex)
                {
                    importError = ex.Message;
                    App.Logger.Log("FET import error: " + ex.ToString());
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();

            if (importError != null)
            {
                FrostyMessageBox.Show("Failed to import FET project:\n\n" + importError, "Import Error");
            }
            else
            {
                FrostyMessageBox.Show(summary, "Import FET Project");
            }
        });
    }
}
