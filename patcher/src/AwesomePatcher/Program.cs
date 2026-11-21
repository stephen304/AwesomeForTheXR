using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AwesomePatcher
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 0) return RunGui(null);
            if (args.Length == 1 && !CommandLine.IsCommand(args[0]) &&
                !args[0].StartsWith("-", StringComparison.Ordinal) && Directory.Exists(args[0]))
                return RunGui(args[0]);

            return CommandLine.Run(args);
        }

        private static int RunGui(string selectedFolder)
        {
            // Explorer can create a console for this executable. Only the GUI detaches.
            FreeConsole();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(PackagePayload.CreateService(), selectedFolder));
            return 0;
        }
    }
}
