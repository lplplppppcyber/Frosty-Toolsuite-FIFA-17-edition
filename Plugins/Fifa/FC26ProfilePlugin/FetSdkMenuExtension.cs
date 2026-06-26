using Frosty.Core;
using Frosty.Core.Sdk;
using Frosty.Controls;
using FrostySdk;
using Microsoft.Win32;

namespace FC26ProfilePlugin
{
    // Tools > Import FET FC26 SDK
    // Lets the user point at FET's net9 FC26SDK.dll; FetSdkImporter converts it to a
    // Frosty-format SDK (TmpProfiles/FC26SDK.dll) so FC26 EBX types resolve without the
    // anti-cheat-blocked memory scan.
    public class FetSdkMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => null;
        public override string MenuItemName => "Import FET FC26 SDK";

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            if (ProfilesLibrary.DataVersion != (int)ProfileVersion.FC26)
            {
                FrostyMessageBox.Show("This importer is only for the FC26 profile.", "Frosty Editor");
                return;
            }

            OpenFileDialog ofd = new OpenFileDialog
            {
                Title = "Select FET-generated FC26SDK.dll",
                Filter = "FET SDK (FC26SDK.dll)|FC26SDK.dll|Assemblies (*.dll)|*.dll"
            };
            if (ofd.ShowDialog() == true)
            {
                if (FetSdkImporter.Import(ofd.FileName, out string error))
                {
                    FrostyMessageBox.Show(
                        "FET SDK imported successfully. Restart Frosty to load the new FC26 SDK.",
                        "Frosty Editor");
                }
                else
                {
                    FrostyMessageBox.Show(
                        "FET SDK import failed:\n\n" + error + "\n\nSee fet_sdk_import.txt for details.",
                        "Frosty Editor");
                }
            }
        });
    }
}
