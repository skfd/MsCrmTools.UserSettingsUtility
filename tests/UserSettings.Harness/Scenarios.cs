using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Xrm.Sdk;

namespace UserSettings.Harness
{
    /// <summary>
    /// The tool against an org that lives in memory. Each scenario is about one of the fix
    /// branches, and says which: run against master it should fail, on the e2e branch it should
    /// not.
    /// </summary>
    public static class Scenarios
    {
        public static List<Scenario> All()
        {
            return new List<Scenario>
            {
                LoadListsEachAreaOnce(),
                ReconnectForgetsTheOldOrg(),
                TypedWorkTimeIsSent(),
                MistypedWorkTimeIsRefused(),
                UserWithoutHomePage(),
                ShiftSelectLoadsOnce(),
                UpdateImpersonatesEachUser()
            };
        }

        // ---- shared steps ----

        private static Scenario Loaded(this Scenario scenario, int users)
        {
            return scenario
                .Do("press Load", r => r.Find<ToolStripButton>("tsbLoadCrmItems").PerformClick())
                .WaitFor("the settings and " + users + " users to arrive",
                    r => r.Find<Panel>("panel1").Enabled && r.Users.Items.Count == users);
        }

        private static Scenario Select(this Scenario scenario, int index)
        {
            return scenario
                .Do("select user " + (index + 1), r =>
                {
                    var item = r.Users.Items[index];
                    r.Users.SelectedItems.Clear();
                    item.Selected = true;
                    r.Users.FocusedItem = item;
                })
                // Every sample user's paging limit is 50, and after Load the list shows No change
                .WaitFor("user " + (index + 1) + "'s settings to show",
                    r => r.Find<ComboBox>("cbbPagingLimit").Text == "50");
        }

        private static Scenario Pause(this Scenario scenario, string label, int ms)
        {
            Stopwatch started = null;
            return scenario.WaitFor(label, r =>
            {
                started = started ?? Stopwatch.StartNew();
                return started.ElapsedMilliseconds >= ms;
            }, ms + 5000);
        }

        private static Scenario Update(this Scenario scenario, params int[] checkedUsers)
        {
            return scenario
                .Do("tick users " + string.Join(", ", checkedUsers.Select(i => i + 1)), r =>
                {
                    foreach (ListViewItem item in r.Users.Items) item.Checked = false;
                    foreach (var i in checkedUsers) r.Users.Items[i].Checked = true;
                    r.Fake.ForgetCalls();
                })
                .Do("press Update", r => r.Find<ToolStripButton>("tsbUpdateUserSettings").PerformClick());
        }

        private static Scenario Finished(this Scenario scenario)
        {
            // The tool ends every update by offering the log, and writes a failed user only there
            return scenario
                .WaitFor("the update to finish", r => r.Dialogs.Any(d => d.Contains("Do you want to open log file?")), 30000)
                .Do("read the tool's log", r =>
                {
                    var errors = r.LogLines.Where(l => l.Contains("was not updated")).ToArray();
                    r.Check(errors.Length == 0, "the tool logged: " + string.Join(" | ", errors));
                });
        }

        private static List<Call> Updates(Run r)
        {
            return r.Fake.Calls.Where(c => c.What == "Update usersettings").ToList();
        }

        private static Guid UserId(int index)
        {
            return new Guid(index + 1, 0, 0, new byte[8]);
        }

        // ---- scenarios ----

        private static Scenario LoadListsEachAreaOnce()
        {
            return new Scenario
                {
                    Name = "load-lists-each-area-once",
                    Why = "fix/sitemap-area-lists: areas doubled up, and the app's name never showed",
                    Org = () => FakeOrg.Sample("Org A", 3)
                }
                .Loaded(3)
                .Do("read the lists", r =>
                {
                    var areas = Ui.Items(r.Find<ComboBox>("cbbSiteMapArea"));
                    var apps = r.Find<ToolStripComboBox>("applist");
                    r.Check(areas.Length == areas.Distinct().Count(),
                        "the area list repeats entries: " + string.Join(", ", areas));
                    r.Check(areas.SequenceEqual(new[] { "No change", "Workplace", "SFA" }),
                        "the area list is not the default sitemap's: " + string.Join(", ", areas));
                    r.Check(Ui.Items(apps).Contains("Contoso Hub"),
                        "the app list does not name the app: " + string.Join(", ", Ui.Items(apps)));
                    r.Check(apps.SelectedItem?.ToString() == "Default",
                        "the app list shows \"" + apps.SelectedItem + "\" while the areas are the default sitemap's");
                });
        }

        private static Scenario ReconnectForgetsTheOldOrg()
        {
            var a = FakeOrg.Sample("Org A", 3, "A_");
            var b = FakeOrg.Sample("Org B", 4, "B_");
            return new Scenario
                {
                    Name = "reconnect-forgets-the-old-org",
                    Why = "fix/reset-on-reconnect: Load after a switch kept the old org's sitemap",
                    Org = () => a
                }
                .Loaded(3)
                .Do("switch to Org B", r =>
                {
                    r.SwitchTo(b);
                    a.ForgetCalls();
                    r.Check(r.Users.Items.Count == 0, "Org A's users are still listed after the switch");
                })
                .Loaded(4)
                .Do("read the lists", r =>
                {
                    var areas = Ui.Items(r.Find<ComboBox>("cbbSiteMapArea")).Skip(1).ToArray();
                    r.Check(areas.Length > 0 && areas.All(x => x.StartsWith("B_")),
                        "after switching to Org B the areas are: " + string.Join(", ", areas));
                    r.Check(a.Calls.Count == 0, "Org A was still asked " + a.Calls.Count + " things after the switch");
                });
        }

        private static Scenario TypedWorkTimeIsSent()
        {
            return new Scenario
                {
                    Name = "typed-work-time-is-sent",
                    Why = "fix/update-null-reference (#51): a typed work time threw on Update",
                    Org = () => FakeOrg.Sample("Org A", 3)
                }
                .Loaded(3)
                .Select(0)
                .Do("type 18:15 as the end time", r => r.Find<ComboBox>("cbbWorkStopTime").Text = "18:15")
                .Update(0)
                .Finished()
                .Do("ask the org what was written", r =>
                {
                    var updates = Updates(r);
                    r.Check(updates.Count == 1, updates.Count + " updates were sent, not 1");
                    var written = updates.FirstOrDefault()?.Written;
                    r.Check(written?.GetAttributeValue<string>("workdaystoptime") == "18:15",
                        "the end time written was \"" + written?.GetAttributeValue<string>("workdaystoptime") + "\"");
                    // Selecting a user fills the form with their settings, and Update applies the
                    // form: that is how one user's settings are copied to others
                    r.Check(written?.GetAttributeValue<string>("workdaystarttime") == "08:00",
                        "the start time written was \"" + written?.GetAttributeValue<string>("workdaystarttime")
                        + "\", not the 08:00 the form showed");
                });
        }

        private static Scenario MistypedWorkTimeIsRefused()
        {
            return new Scenario
                {
                    Name = "mistyped-work-time-is-refused",
                    Why = "fix/update-null-reference (#51): \"18.00\" from the issue's screenshot",
                    Org = () => FakeOrg.Sample("Org A", 3)
                }
                .Loaded(3)
                .Select(0)
                .Do("type 18.00 as the end time", r => r.Find<ComboBox>("cbbWorkStopTime").Text = "18.00")
                .Update(0)
                .WaitFor("the tool to say what is wrong", r => r.Dialogs.Any(d => d.Contains("not a valid work time")), 5000)
                .Pause("give a stray update time to arrive", 1000)
                .Do("check nothing was written", r => r.Check(Updates(r).Count == 0, "the mistyped time was sent anyway"));
        }

        private static Scenario UserWithoutHomePage()
        {
            return new Scenario
                {
                    Name = "user-without-home-page",
                    Why = "fix/update-null-reference (#51): a user with no home page threw on select and on Update",
                    Org = () =>
                    {
                        var org = FakeOrg.Sample("Org A", 3);
                        org.Settings[UserId(0)].Attributes.Remove("homepagearea");
                        org.Settings[UserId(0)].Attributes.Remove("homepagesubarea");
                        return org;
                    }
                }
                .Loaded(3)
                .Select(0)
                .Do("check the home page reads No change", r =>
                    r.Check(r.Find<ComboBox>("cbbSiteMapArea").SelectedIndex == 0,
                        "the area list's index is " + r.Find<ComboBox>("cbbSiteMapArea").SelectedIndex))
                .Do("pick a paging limit of 100", r => r.Find<ComboBox>("cbbPagingLimit").SelectedItem = "100")
                .Update(0)
                .Finished()
                .Do("ask the org what was written", r =>
                {
                    var written = Updates(r).FirstOrDefault()?.Written;
                    r.Check(written?.GetAttributeValue<int>("paginglimit") == 100, "the paging limit was not written");
                    r.Check(written == null || !written.Contains("homepagearea"), "a home page was written although none was chosen");
                });
        }

        private static Scenario ShiftSelectLoadsOnce()
        {
            long selectMs = 0;
            return new Scenario
                {
                    Name = "shift-select-loads-once",
                    Why = "perf/user-selection: selecting 24 users fetched 24 settings rows on the UI thread",
                    Org = () =>
                    {
                        var org = FakeOrg.Sample("Org A", 24);
                        org.SettingsLatencyMs = 150;
                        return org;
                    }
                }
                .Loaded(24)
                .Do("shift-select all 24 users", r =>
                {
                    r.Fake.ForgetCalls();
                    var clock = Stopwatch.StartNew();
                    // What a shift-click does: one selection change per row, then focus on the last
                    foreach (ListViewItem item in r.Users.Items) item.Selected = true;
                    r.Users.FocusedItem = r.Users.Items[23];
                    selectMs = clock.ElapsedMilliseconds;
                })
                .WaitFor("a settings row to be read", r => r.Fake.Calls.Any(c => c.What.StartsWith("RetrieveMultiple usersettings")), 10000)
                .Pause("let any more reads arrive", 1500)
                .Do("count the reads", r =>
                {
                    var reads = r.Fake.Calls.Where(c => c.What.StartsWith("RetrieveMultiple usersettings")).ToList();
                    r.Check(selectMs < 500, "selecting held the window for " + selectMs + "ms");
                    r.Check(reads.Count == 1, reads.Count + " settings rows were read for one selection, not 1");
                    r.Check(reads.All(c => c.ThreadId != r.UiThreadId), "settings were read on the UI thread");
                    r.Check(reads.LastOrDefault()?.What.EndsWith(UserId(23).ToString()) == true,
                        "the row read was not the focused user's: " + reads.LastOrDefault()?.What);
                });
        }

        private static Scenario UpdateImpersonatesEachUser()
        {
            return new Scenario
                {
                    Name = "update-impersonates-each-user",
                    Why = "baseline: Update writes only what was chosen, as each ticked user",
                    Org = () => FakeOrg.Sample("Org A", 5)
                }
                .Loaded(5)
                .Do("pick a paging limit of 100", r => r.Find<ComboBox>("cbbPagingLimit").SelectedItem = "100")
                .Update(1, 2, 3)
                .Finished()
                .Do("ask the org what was written", r =>
                {
                    var updates = Updates(r);
                    r.Check(updates.Count == 3, updates.Count + " updates were sent, not 3; the org was asked: "
                        + string.Join(" | ", r.Fake.Calls.Select(c => c.What)));
                    foreach (var update in updates)
                    {
                        r.Check(update.CallerId == update.Written.Id,
                            "usersettings " + update.Written.Id + " was written as " + update.CallerId);
                        var keys = update.Written.Attributes.Keys.Where(k => k != "usersettingsid").OrderBy(k => k).ToArray();
                        r.Check(keys.SequenceEqual(new[] { "paginglimit" }),
                            "an update wrote more than the paging limit: " + string.Join(", ", keys));
                    }
                    r.Check(r.Fake.CallerId == Guid.Empty, "the service was left impersonating " + r.Fake.CallerId);
                });
        }
    }
}
