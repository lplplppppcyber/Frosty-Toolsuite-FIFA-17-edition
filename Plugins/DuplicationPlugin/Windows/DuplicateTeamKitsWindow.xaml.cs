using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Managers;
using System;
using System.Windows;

namespace DuplicationPlugin.Windows
{
    public partial class DuplicateTeamKitsWindow : FrostyDockableWindow
    {
        public string NewTeamFolderName { get; private set; }
        public string DestinationPath { get; private set; }

        private readonly string sourceTeamFolder;

        public DuplicateTeamKitsWindow(string inSourceTeamFolder)
        {
            InitializeComponent();

            sourceTeamFolder = inSourceTeamFolder;
            sourceFolderTextBox.Text = inSourceTeamFolder;

            string sourceName = inSourceTeamFolder.Substring(inSourceTeamFolder.LastIndexOf('/') + 1);
            newNameTextBox.Text = sourceName;

            pathSelector.ItemsSource = App.AssetManager.EnumerateEbx();
        }

        private void FrostyDockableWindow_FrostyLoaded(object sender, EventArgs e)
        {
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                string path = entry.Path.Replace('\\', '/');
                // Select an asset one level inside the team folder so the path selector
                // lands on the right parent directory.
                if (path.StartsWith(sourceTeamFolder + "/", StringComparison.OrdinalIgnoreCase))
                {
                    pathSelector.SelectAsset(entry);
                    break;
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void DuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            string newName = newNameTextBox.Text.Replace('\\', '/').Trim('/').Trim();

            if (string.IsNullOrEmpty(newName))
            {
                FrostyMessageBox.Show("New team folder name cannot be empty.", "Team Kit Duplicator");
                return;
            }

            if (newName.Contains("//") || newName.Contains(" "))
            {
                FrostyMessageBox.Show("Name contains invalid characters (no spaces or double slashes).", "Team Kit Duplicator");
                return;
            }

            int lastUnderscore = newName.LastIndexOf('_');
            if (lastUnderscore < 0)
            {
                FrostyMessageBox.Show(
                    "New team name must end with a numeric ID.\nExample: new_team_9999",
                    "Team Kit Duplicator");
                return;
            }

            string idPart = newName.Substring(lastUnderscore + 1);
            int dummy;
            if (!int.TryParse(idPart, out dummy))
            {
                FrostyMessageBox.Show(
                    "New team name must end with a numeric ID.\nExample: new_team_9999",
                    "Team Kit Duplicator");
                return;
            }

            string sourceName = sourceTeamFolder.Substring(sourceTeamFolder.LastIndexOf('/') + 1);
            if (newName.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
            {
                FrostyMessageBox.Show("New name must be different from the source.", "Team Kit Duplicator");
                return;
            }

            string destPath = pathSelector.SelectedPath;
            if (string.IsNullOrEmpty(destPath))
            {
                FrostyMessageBox.Show("Select a destination folder in the tree.", "Team Kit Duplicator");
                return;
            }

            NewTeamFolderName = newName;
            DestinationPath = destPath.Replace('\\', '/');

            DialogResult = true;
            Close();
        }
    }
}
