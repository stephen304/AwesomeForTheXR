using System.IO;
using AwesomePatcher.Core;

namespace AwesomePatcher
{
    internal static class PackagePayload
    {
        internal static PatcherService CreateService()
        {
            // ILRepack puts the patcher and core in one assembly for distribution.
            var patcher = typeof(PackagePayload).Assembly;
            if (patcher != typeof(PatcherService).Assembly) return PatcherService.ForGame();

            return PatcherService.ForGamePackage(name =>
                patcher.GetManifestResourceStream("AwesomePatcher.Bundle." + name)
                ?? throw new FileNotFoundException("Missing bundled file: " + name));
        }
    }
}
