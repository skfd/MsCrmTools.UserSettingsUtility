using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace UserSettings.Harness
{
    /// <summary>
    /// The User Settings Utility, driven in a window of its own. Run it through tests\fake.ps1 or
    /// tests\e2e.ps1 rather than directly.
    ///
    ///   UserSettings.Harness.exe fake &lt;output folder&gt; [scenario]
    ///   UserSettings.Harness.exe org &lt;org url&gt; &lt;username&gt; &lt;token cache&gt; &lt;output folder&gt;
    ///
    /// Prints one line per scenario and exits non-zero if any failed. A shot of the window after
    /// every step, and the dialogs the tool put up, land in the output folder.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            if (args.Length >= 2 && args[0] == "fake")
            {
                var only = args.Length > 2 ? args[2] : null;
                var scenarios = Scenarios.All().Where(s => only == null || s.Name == only).ToList();
                if (scenarios.Count == 0)
                {
                    Console.Error.WriteLine("No scenario called " + only);
                    return 2;
                }

                var failed = 0;
                foreach (var scenario in scenarios)
                {
                    if (!Report(scenario.Name, scenario.Why, scenario.Play(args[1]))) failed++;
                }

                Console.WriteLine();
                Console.WriteLine((scenarios.Count - failed) + " of " + scenarios.Count + " passed");
                return failed == 0 ? 0 : 1;
            }

            if (args.Length >= 5 && args[0] == "org")
            {
                var client = RealOrg.Connect(args[1], args[2], args[3]);
                if (!client.IsReady)
                {
                    Console.Error.WriteLine("Could not sign in: " + client.LastCrmError);
                    return 3;
                }

                return Report("real-org-own-settings", "against " + client.ConnectedOrgFriendlyName,
                    RealOrg.Play(client, args[4])) ? 0 : 1;
            }

            Console.Error.WriteLine("usage: fake <output folder> [scenario] | org <url> <username> <token cache> <output folder>");
            return 2;
        }

        private static bool Report(string name, string why, List<string> failures)
        {
            Console.WriteLine((failures.Count == 0 ? "PASS  " : "FAIL  ") + name + "  - " + why);
            foreach (var failure in failures) Console.WriteLine("        " + failure);
            return failures.Count == 0;
        }
    }
}
