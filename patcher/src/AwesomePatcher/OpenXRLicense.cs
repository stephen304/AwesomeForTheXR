using System;
using System.IO;

namespace AwesomePatcher
{
    internal static class OpenXRLicense
    {
        internal static string ReadText()
        {
            var stream = typeof(OpenXRLicense).Assembly.GetManifestResourceStream("OpenXR-LICENSE.txt")
                ?? throw new InvalidOperationException("The embedded OpenXR license is missing.");
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }
    }
}
