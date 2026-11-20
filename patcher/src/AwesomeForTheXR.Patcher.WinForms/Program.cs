using System;
using System.Windows.Forms;
using AwesomeForTheXR.Patcher.Core;

namespace AwesomeForTheXR.Patcher.WinForms
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string selectedFolder = args.Length == 1 ? args[0] : null;
            Application.Run(new MainForm(PatcherService.ForGame(), selectedFolder));
        }
    }
}
