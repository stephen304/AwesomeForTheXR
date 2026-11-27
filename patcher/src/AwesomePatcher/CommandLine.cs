using System;
using System.IO;
using AwesomePatcher.Core;

namespace AwesomePatcher
{
    internal static class CommandLine
    {
        private static readonly string Usage =
            "Usage: AwesomePatcher.exe [GAME_FOLDER]" + Environment.NewLine +
            "       AwesomePatcher.exe <verify|patch|revert> [--game-dir PATH] [-y]" + Environment.NewLine +
            "       AwesomePatcher.exe --help | --openxr-license | --licenses";

        internal static bool IsCommand(string value) => value == "verify" || value == "patch" || value == "revert";

        internal static int Run(string[] args)
        {
            if (args.Length == 1 && args[0] == "--openxr-license")
            {
                Console.Write(OpenXRLicense.ReadText());
                return 0;
            }
            if (args.Length == 1 && args[0] == "--licenses")
            {
                Console.Write(OpenXRLicense.ReadAllNotices());
                return 0;
            }
            if (args.Length == 1 && args[0] == "--help")
            {
                Console.WriteLine(Usage);
                Console.WriteLine("patch installs or updates from the verified original. -y skips confirmation.");
                return 0;
            }
            if (args.Length == 0 || !IsCommand(args[0]))
                return UsageError();

            string folder = null;
            bool assumeYes = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "-y")
                    assumeYes = true;
                else if (args[i] == "--game-dir" && folder == null && i + 1 < args.Length &&
                    !string.IsNullOrWhiteSpace(args[i + 1]) && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    folder = args[++i];
                else
                    return UsageError();
            }

            try
            {
                folder = folder ?? GameDiscovery.FindGameFolder();
                if (folder == null)
                {
                    Console.Error.WriteLine("Steam discovery could not select a single game folder. Supply --game-dir PATH.");
                    return 1;
                }
                return Run(args[0], Path.GetFullPath(folder), assumeYes);
            }
            catch (OperationCanceledException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int UsageError()
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        private static int Run(string command, string folder, bool assumeYes)
        {
            Console.WriteLine("Game folder: " + folder);
            bool patch = command == "patch";
            if (command != "verify" && !assumeYes)
            {
                Console.Write("{0} the game in \"{1}\"? [y/N] ", patch ? "Patch" : "Revert", folder);
                string answer = Console.ReadLine()?.Trim();
                if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Cancelled.");
                    return 3;
                }
            }

            var service = GameFileWorker.CreateCoordinator();
            var status = service.Inspect(folder);
            Console.WriteLine(status.Message);
            if (command == "verify")
                return status.CanPatch || status.IsCurrent ? 0 : 1;

            if (patch)
            {
                if (status.IsCurrent) return 0;
                if (!status.CanPatch) return 1;
                service.Patch(folder);
                Console.WriteLine("Patch applied. " + service.Inspect(folder).Message);
            }
            else
            {
                if (!status.CanRevert)
                {
                    Console.Error.WriteLine("There is no verified installation to revert.");
                    return 1;
                }
                bool artworkConfirmed = service.Revert(folder);
                if (!status.FilesOriginal) Console.WriteLine("Original game files restored.");
                if (!artworkConfirmed)
                {
                    Console.WriteLine(PatchCoordinator.UnverifiedCleanup);
                    if (status.FilesOriginal) return 1;
                }
                else if (status.FilesOriginal) Console.WriteLine("SteamVR artwork cleanup complete.");
            }
            return 0;
        }
    }
}
