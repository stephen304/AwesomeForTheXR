using System;
using System.IO;

namespace AwesomePatcher
{
    internal static class OpenXRLicense
    {
        internal static string ReadText() => ReadResource("OpenXR-LICENSE.txt");

        internal static string ReadAllNotices() =>
            "AwesomeForTheXR" + Environment.NewLine + ReadResource("LICENSE.txt") + Environment.NewLine +
            "Mono.Cecil" + Environment.NewLine + ReadResource("Mono.Cecil-LICENSE.txt") + Environment.NewLine +
            "OpenXR" + Environment.NewLine + ReadText();

        private static string ReadResource(string name)
        {
            var stream = typeof(OpenXRLicense).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("Missing embedded notice: " + name);
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }
    }
}
