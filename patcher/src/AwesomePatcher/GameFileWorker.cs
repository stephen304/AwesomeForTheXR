using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using AwesomePatcher.Core;

namespace AwesomePatcher
{
    internal static class GameFileWorker
    {
        private const int AccessDenied = 5;

        internal static PatchCoordinator CreateCoordinator() =>
            new PatchCoordinator(PackagePayload.CreateService(), new SteamVrCatalog(), ChangeFiles, CanElevate);

        internal static int Run(string[] args)
        {
            if (args.Length < 3 || args.Length > 4 || (args.Length == 4 && args[3] != "--retry") ||
                (args[1] != "patch" && args[1] != "revert")) return 2;
            try
            {
                PackagePayload.CreateService().Change(Path.GetFullPath(args[2]), args[1] == "revert", args.Length == 4,
                    secureDirectories: ElevatedDirectories.NativeWindows && IsAdministrator());
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                if (ex is UnauthorizedAccessException || ex is IOException && ex.HResult == unchecked((int)0x80070005))
                    return AccessDenied;
                // The elevated process has its own console. Preserve the Windows error for its caller.
                return (ex is IOException && ex.HResult < 0) ? ex.HResult : 1;
            }
        }

        private static bool CanElevate() => ElevatedDirectories.NativeWindows && !IsAdministrator();

        private static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static void ChangeFiles(string folder, bool revert, bool elevated)
        {
            string command = revert ? "revert" : "patch";
            // A final dot also quotes volume roots correctly in the Windows argument parser.
            string directory = Path.Combine(Path.GetFullPath(folder), ".");
            var start = new ProcessStartInfo(Application.ExecutablePath, "--game-files " + command + " \"" + directory + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
            };
            if (elevated)
            {
                start.Arguments += " --retry";
                start.UseShellExecute = true;
                start.RedirectStandardError = false;
                start.Verb = "runas";
                start.WindowStyle = ProcessWindowStyle.Hidden;
            }

            try
            {
                using (var child = Process.Start(start))
                {
                    string detail = elevated ? null : child.StandardError.ReadToEnd().Trim();
                    child.WaitForExit();
                    if (child.ExitCode == 0) return;
                    if (child.ExitCode == AccessDenied)
                        throw new UnauthorizedAccessException(detail ?? "Administrator access was denied in " + folder);
                    if (child.ExitCode == ElevatedDirectories.CannotSecure)
                        throw new IOException(detail ?? ElevatedDirectories.Refusal);
                    string reason = child.ExitCode < 0 ? Marshal.GetExceptionForHR(child.ExitCode)?.Message : null;
                    throw new IOException(string.IsNullOrEmpty(detail) ? "Could not change game files in " + folder + ". " + reason : detail);
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("Administrator permission was declined.", ex);
            }
        }
    }
}
