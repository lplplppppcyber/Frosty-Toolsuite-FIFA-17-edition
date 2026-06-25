using Frosty.Core.Attributes;
using FC26ProfilePlugin;
using FrostySdk;
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: ComVisible(false)]
[assembly: Guid("a7f3c26b-1e8d-4b9a-b320-fc26ea5f0036")]

[assembly: PluginDisplayName("EA SPORTS FC 26 Profile")]
[assembly: PluginAuthor("FrostyToolsuite")]
[assembly: PluginVersion("1.0.0.0")]

[assembly: RegisterProfile(typeof(FC26Profile))]
