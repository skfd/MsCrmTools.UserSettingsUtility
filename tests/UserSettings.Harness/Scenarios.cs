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
                UpdateImpersonatesEachUser(),
                TimeZoneForTwoUsers("time-zone-plain", "nothing missing", true),
                TimeZoneForTwoUsers("time-zone-no-tab", "both users have an area but no tab", true,
                    org => { Forget(org, 0, "homepagesubarea"); Forget(org, 1, "homepagesubarea"); }),
                TimeZoneForTwoUsers("time-zone-no-home", "both users have neither area nor tab", true,
                    org => { Forget(org, 0, "homepagearea", "homepagesubarea"); Forget(org, 1, "homepagearea", "homepagesubarea"); }),
                FailedReloadThenUpdate(),
                TimeZoneForTwoUsers("time-zone-first-no-tab", "only the first user clicked has no tab", true,
                    org => Forget(org, 0, "homepagesubarea")),
                TimeZoneForTwoUsers("time-zone-ticks-only", "no tab, but the users are ticked and never clicked", false,
                    org => { Forget(org, 0, "homepagesubarea"); Forget(org, 1, "homepagesubarea"); }),
                TimeZoneForTwoUsers("time-zone-users-differ", "user 1 has 100 a page from 09:00, user 2 has 50 from 08:00", true,
                    org =>
                    {
                        org.Settings[UserId(0)]["paginglimit"] = 100;
                        org.Settings[UserId(0)]["workdaystarttime"] = "09:00";
                    }),
                ChangedFieldsAreMarked(),
                NothingChangedSendsNothing(),
                ResetThenPickSendsTheValue(),
                AreaFromAnotherAppIsSent(),
                EachRowCountsItsOwnChanges(),
                CountsFallAfterUpdate(),
                FilterIgnoresTheCounts(),
                CountWaitsForTheRead(),
                OlderReadCannotOverwriteCounts(),
                FailedReadIsNotACount(),
                CountsCoverEveryBatch()
            };
        }

        private static void Forget(FakeOrg org, int user, params string[] attributes)
        {
            foreach (var attribute in attributes) org.Settings[UserId(user)].Attributes.Remove(attribute);
        }

        /// <summary>Presses Update and keeps what it threw, which comes straight out of PerformClick.</summary>
        internal static void PressUpdate(Run r, string when)
        {
            Console.WriteLine("    " + when + ": area index " + r.Find<ComboBox>("cbbSiteMapArea").SelectedIndex
                              + ", tab index " + r.Find<ComboBox>("cbbSiteMapSubArea").SelectedIndex);
            try
            {
                r.Find<ToolStripButton>("tsbUpdateUserSettings").PerformClick();
            }
            catch (Exception ex)
            {
                r.Thrown.Add(when + ": " + Ui.Where(ex));
            }
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
                    // Update sends what was changed, not the form the clicked user filled
                    r.Check(written?.Contains("workdaystarttime") == false,
                        "the start time was written although nobody changed it");
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

        /// <summary>
        /// Select a couple of users, set the time zone, save - and if that throws, do what the
        /// person who hit it did: flip the app list and save again.
        /// </summary>
        private static Scenario TimeZoneForTwoUsers(string name, string why, bool clickRows, Action<FakeOrg> data = null)
        {
            var thrownByFirstSave = 0;
            return new Scenario
                {
                    Name = name,
                    Why = "two users, a time zone, Update: " + why,
                    Survive = true,
                    Org = () =>
                    {
                        var org = FakeOrg.Sample("Org A", 3);
                        data?.Invoke(org);
                        return org;
                    }
                }
                .Loaded(3)
                .Do(clickRows ? "click two users" : "leave the rows alone", r =>
                {
                    if (!clickRows) return;
                    foreach (var i in new[] { 0, 1 })
                    {
                        r.Users.Items[i].Selected = true;
                        r.Users.FocusedItem = r.Users.Items[i];
                    }
                })
                .Pause("the selection to settle", 1000)
                .Do("set the time zone", r =>
                {
                    var zones = r.Find<ComboBox>("cbbTimeZones");
                    zones.SelectedIndex = zones.Items.Count - 1;
                })
                .Do("tick them", r =>
                {
                    r.Users.Items[0].Checked = true;
                    r.Users.Items[1].Checked = true;
                    r.Fake.ForgetCalls();
                })
                .Do("press Update", r =>
                {
                    PressUpdate(r, "first save");
                    thrownByFirstSave = r.Thrown.Count;
                })
                .WaitFor("the update to finish or throw",
                    r => thrownByFirstSave > 0 || r.Dialogs.Any(d => d.Contains("Do you want to open log file?")), 30000)
                .Do("flip the app list there and back if it threw", r =>
                {
                    if (thrownByFirstSave == 0) return;
                    var apps = r.Find<ToolStripComboBox>("applist");
                    var was = apps.SelectedIndex;
                    apps.SelectedIndex = was == 0 ? 1 : 0;
                    apps.SelectedIndex = was;
                })
                .Do("press Update again if it threw", r =>
                {
                    if (thrownByFirstSave > 0) PressUpdate(r, "after the app list");
                })
                .WaitFor("the second update to finish or throw",
                    r => r.Thrown.Count > thrownByFirstSave || r.Dialogs.Any(d => d.Contains("Do you want to open log file?")), 30000)
                .Do("ask the org what was written", r =>
                {
                    foreach (var thrown in r.Thrown) Console.WriteLine("    threw " + thrown);
                    foreach (var dialog in r.Dialogs) Console.WriteLine("    dialog " + dialog);
                    var updates = Updates(r);
                    foreach (var update in updates)
                    {
                        Console.WriteLine("    wrote " + update.Written.Id + ": " + string.Join(", ", update.Written.Attributes
                            .Where(a => a.Key != "usersettingsid").Select(a => a.Key + "=" + a.Value)));
                    }

                    r.Check(r.Thrown.Count == 0, "the tool threw " + r.Thrown.Count + " time(s)");
                    r.Check(updates.Count == 2, updates.Count + " updates were sent, not 2");
                    foreach (var update in updates)
                    {
                        var keys = update.Written.Attributes.Keys.Where(k => k != "usersettingsid").OrderBy(k => k).ToArray();
                        r.Check(keys.SequenceEqual(new[] { "timezonecode" }),
                            "an update wrote more than the time zone: " + string.Join(", ", keys));
                    }
                });
        }

        /// <summary>
        /// Load empties three lists before it starts and only refills them if it works, while the
        /// form stays enabled from the Load before.
        /// </summary>
        private static Scenario FailedReloadThenUpdate()
        {
            return new Scenario
                {
                    Name = "failed-reload-then-update",
                    Why = "a second Load fails, then Update, then the app list, then Update",
                    Survive = true,
                    Org = () => FakeOrg.Sample("Org A", 3)
                }
                .Loaded(3)
                .Do("press Load with the org down", r =>
                {
                    r.Fake.Down = true;
                    r.Find<ToolStripButton>("tsbLoadCrmItems").PerformClick();
                })
                .WaitFor("the tool to report it", r => r.Dialogs.Any(d => d.Contains("An error occured")))
                .Do("bring the org back and tick a user", r =>
                {
                    r.Fake.Down = false;
                    Console.WriteLine("    form enabled " + r.Find<Panel>("panel1").Enabled
                                      + ", time zones listed " + r.Find<ComboBox>("cbbTimeZones").Items.Count
                                      + ", time zone index " + r.Find<ComboBox>("cbbTimeZones").SelectedIndex);
                    r.Users.Items[0].Checked = true;
                })
                .Do("press Update", r => PressUpdate(r, "first save"))
                .Pause("anything it started to finish", 1000)
                .Do("flip the app list and press Update", r =>
                {
                    var apps = r.Find<ToolStripComboBox>("applist");
                    var was = apps.SelectedIndex;
                    apps.SelectedIndex = was == 0 ? 1 : 0;
                    apps.SelectedIndex = was;
                    PressUpdate(r, "after the app list");
                })
                .Pause("that to finish", 1000)
                .Do("report", r =>
                {
                    foreach (var thrown in r.Thrown) Console.WriteLine("    threw " + thrown);
                    foreach (var dialog in r.Dialogs.Skip(1)) Console.WriteLine("    dialog " + dialog);
                    r.Check(r.Thrown.Count == 0, "the tool threw " + r.Thrown.Count + " time(s)");
                });
        }

        private static void CheckMarks(Run r, int count, params string[] labels)
        {
            var button = r.Find<ToolStripButton>("tsbUpdateUserSettings");
            r.Check(button.Text.EndsWith("(" + count + " changed)") == (count > 0), "the button reads \"" + button.Text + "\"");
            foreach (var label in new[] { "label3", "label4", "label6" })
            {
                r.Check(r.Find<Control>(label).Font.Bold == labels.Contains(label), label + " bold is " + r.Find<Control>(label).Font.Bold);
            }
        }

        private static Scenario ChangedFieldsAreMarked()
        {
            return new Scenario
                {
                    Name = "changed-fields-are-marked",
                    Why = "fix/update-only-changes: the form shows what Update will write",
                    Org = () => FakeOrg.Sample("Org A", 3)
                }
                .Loaded(3)
                .Select(0)
                .Do("nothing is marked after clicking a user", r => CheckMarks(r, 0))
                .Do("change the paging limit", r =>
                {
                    r.Find<ComboBox>("cbbPagingLimit").SelectedItem = "100";
                    CheckMarks(r, 1, "label3");
                })
                .Do("type a start time and set the time zone", r =>
                {
                    r.Find<ComboBox>("cbbWorkStartTime").Text = "08:45";
                    var zones = r.Find<ComboBox>("cbbTimeZones");
                    zones.SelectedIndex = zones.Items.Count - 1;
                    CheckMarks(r, 3, "label3", "label4", "label6");
                    var tip = r.Find<ToolStripButton>("tsbUpdateUserSettings").ToolTipText;
                    r.Check(tip.Contains("Records per page") && tip.Contains("Time zone") && tip.Contains("Start time"), "the tooltip reads " + tip);
                })
                .Do("put the paging limit back", r =>
                {
                    r.Find<ComboBox>("cbbPagingLimit").SelectedItem = "50";
                    CheckMarks(r, 2, "label4", "label6");
                })
                .Do("press Reset", r =>
                {
                    r.Find<ToolStripButton>("tsbReset").PerformClick();
                    CheckMarks(r, 0);
                });
        }

        private static string Counts(Run r)
        {
            return string.Join(",", r.Users.Items.Cast<ListViewItem>().Select(i => "[" + i.SubItems[3].Text + "]"));
        }

        private static Scenario Tick(this Scenario scenario, params int[] users)
        {
            return scenario.Do("tick users " + string.Join(", ", users.Select(i => i + 1)), r =>
            {
                foreach (ListViewItem item in r.Users.Items) item.Checked = false;
                foreach (var i in users) r.Users.Items[i].Checked = true;
            });
        }

        private static Scenario CountsRead(this Scenario scenario, string counts)
        {
            return scenario.WaitFor("the rows to read " + counts, r => Counts(r) == counts, 10000);
        }

        private static Scenario EachRowCountsItsOwnChanges()
        {
            var currency = Guid.NewGuid();
            return new Scenario
                {
                    Name = "each-row-counts-its-own-changes",
                    Why = "feat/per-user-update-count: a ticked row counts what differs from that user's stored settings",
                    Org = () =>
                    {
                        var org = FakeOrg.Sample("Org A", 3);
                        org.Settings[UserId(1)]["timezonecode"] = 85;
                        org.Currencies.Add(new Entity("transactioncurrency", currency) { ["currencyname"] = "Dollar" });
                        foreach (var row in org.Settings.Values)
                            row["transactioncurrencyid"] = new EntityReference("transactioncurrency", currency);
                        return org;
                    }
                }
                .Loaded(3)
                .Tick(0, 1)
                .CountsRead("[0],[0],[]")
                .Do("set the time zone user 2 already has", r =>
                {
                    var zones = r.Find<ComboBox>("cbbTimeZones");
                    zones.SelectedIndex = zones.Items.Count - 1;
                })
                .CountsRead("[1],[0],[]")
                .Do("pick a paging limit of 100", r => r.Find<ComboBox>("cbbPagingLimit").SelectedItem = "100")
                .CountsRead("[2],[1],[]")
                .Do("pick the choice and the lookup everyone already has", r =>
                {
                    r.Find<ComboBox>("cbbTrackMessages").SelectedIndex = 2;
                    r.Find<ComboBox>("cbbCurrencies").SelectedIndex = 1;
                    r.Check(r.Find<ToolStripButton>("tsbUpdateUserSettings").Text.Contains("(4 changed)"),
                        "the button reads " + r.Find<ToolStripButton>("tsbUpdateUserSettings").Text);
                })
                .CountsRead("[2],[1],[]")
                .Tick(1, 2)
                .CountsRead("[],[1],[2]")
                .Do("press Reset", r => r.Find<ToolStripButton>("tsbReset").PerformClick())
                .CountsRead("[],[0],[0]");
        }

        private static Scenario CountsFallAfterUpdate()
        {
            return new Scenario
                {
                    Name = "counts-fall-after-update",
                    Why = "feat/per-user-update-count: the stored settings are read again after Update, not as a user it impersonated",
                    Org = () => FakeOrg.Sample("Org A", 3)
                }
                .Loaded(3)
                .Do("pick a paging limit of 100", r => r.Find<ComboBox>("cbbPagingLimit").SelectedItem = "100")
                .Tick(0, 1)
                .CountsRead("[1],[1],[]")
                .Update(0, 1)
                .Finished()
                .CountsRead("[0],[0],[]")
                .Do("ask the org who read them", r =>
                {
                    var reads = r.Fake.Calls.Where(c => c.What.StartsWith("Bulk read")).ToList();
                    r.Check(reads.Count == 1, reads.Count + " bulk reads followed the update, not 1");
                    r.Check(reads.All(c => c.CallerId == Guid.Empty), "the read ran as " + reads.FirstOrDefault()?.CallerId);
                    r.Check(reads.All(c => c.ThreadId != r.UiThreadId), "the read ran on the UI thread");
                });
        }

        private static Scenario FilterIgnoresTheCounts()
        {
            return new Scenario
                {
                    Name = "filter-ignores-the-counts",
                    Why = "feat/per-user-update-count: typing 1 finds user 01, not every row with a count of 1",
                    Org = () => FakeOrg.Sample("Org A", 3)
                }
                .Loaded(3)
                .Do("pick a paging limit of 100", r => r.Find<ComboBox>("cbbPagingLimit").SelectedItem = "100")
                .Tick(0, 1, 2)
                .CountsRead("[1],[1],[1]")
                .Do("type 1 in the filter", r => r.Find<TextBox>("txtFilter").Text = "1")
                .Pause("let the filter run", 500)
                .Do("count the rows", r => r.Check(r.Users.Items.Count == 1, r.Users.Items.Count + " rows match, not 1"));
        }

        private static Scenario CountWaitsForTheRead()
        {
            return new Scenario
                {
                    Name = "count-waits-for-the-read",
                    Why = "feat/per-user-update-count: a row says nothing about settings that have not arrived",
                    Org = () =>
                    {
                        var org = FakeOrg.Sample("Org A", 3);
                        org.BulkSettingsLatencyMs.Enqueue(3000);
                        return org;
                    }
                }
                .Loaded(3)
                .Do("pick a paging limit of 100", r => r.Find<ComboBox>("cbbPagingLimit").SelectedItem = "100")
                .Tick(0)
                .Do("look at the row before the answer", r =>
                    r.Check(Counts(r) == "[...],[],[]", "with no answer yet the rows read " + Counts(r)))
                .CountsRead("[1],[],[]");
        }

        private static Scenario OlderReadCannotOverwriteCounts()
        {
            return new Scenario
                {
                    Name = "older-read-cannot-overwrite-counts",
                    Why = "feat/per-user-update-count: a slow read from before Update lands after the read that followed it",
                    Org = () =>
                    {
                        var org = FakeOrg.Sample("Org A", 3);
                        org.BulkSettingsLatencyMs.Enqueue(6000);
                        return org;
                    }
                }
                .Loaded(3)
                .Do("pick a paging limit of 100", r => r.Find<ComboBox>("cbbPagingLimit").SelectedItem = "100")
                .Update(0)
                .Finished()
                .CountsRead("[0],[],[]")
                .Pause("let the older read land", 6500)
                .Do("look at the row again", r => r.Check(Counts(r) == "[0],[],[]", "the old answer won: " + Counts(r)));
        }

        private static Scenario FailedReadIsNotACount()
        {
            return new Scenario
                {
                    Name = "failed-read-is-not-a-count",
                    Why = "feat/per-user-update-count: a read that failed leaves a question mark, not a number",
                    Org = () => FakeOrg.Sample("Org A", 3)
                }
                .Loaded(3)
                .Do("pick a paging limit of 100", r => r.Find<ComboBox>("cbbPagingLimit").SelectedItem = "100")
                .Tick(0)
                .CountsRead("[1],[],[]")
                .Do("lose the connection and press Update", r =>
                {
                    r.Fake.Down = true;
                    r.Find<ToolStripButton>("tsbUpdateUserSettings").PerformClick();
                })
                .WaitFor("the update to finish", r => r.Dialogs.Any(d => d.Contains("Do you want to open log file?")), 30000)
                .CountsRead("[?],[],[]");
        }

        private static Scenario CountsCoverEveryBatch()
        {
            long tickMs = 0;
            return new Scenario
                {
                    Name = "counts-cover-every-batch",
                    Why = "feat/per-user-update-count: 1200 users are read 500 at a time, and ticking them all stays quick",
                    Org = () => FakeOrg.Sample("Org A", 1200)
                }
                .Loaded(1200)
                .Do("pick a paging limit of 100", r => r.Find<ComboBox>("cbbPagingLimit").SelectedItem = "100")
                .Do("tick everyone", r =>
                {
                    var clock = Stopwatch.StartNew();
                    var selector = Ui.Field<object>(r.Control, "userSelector1");
                    selector.GetType().GetMethod("llCheckAll_LinkClicked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        .Invoke(selector, new object[] { null, null });
                    tickMs = clock.ElapsedMilliseconds;
                    Console.WriteLine("    Check all took " + tickMs + "ms for " + r.Users.Items.Count + " users");
                })
                .WaitFor("every row to read 1", r => r.Users.Items.Cast<ListViewItem>().All(i => i.SubItems[3].Text == "1"), 10000)
                .Do("count the reads", r =>
                {
                    var reads = r.Fake.Calls.Count(c => c.What.StartsWith("Bulk read"));
                    r.Check(reads == 3, reads + " reads for 1200 users, not 3");
                    r.Check(tickMs < 3000, "ticking held the window for " + tickMs + "ms");
                });
        }

        private static Scenario NothingChangedSendsNothing()
        {
            return new Scenario
                {
                    Name = "nothing-changed-sends-nothing",
                    Why = "fix/update-only-changes: Update after only clicking a user writes nothing and says so",
                    Org = () => FakeOrg.Sample("Org A", 3)
                }
                .Loaded(3)
                .Select(0)
                .Update(1)
                .WaitFor("the tool to say so", r => r.Dialogs.Any(d => d.Contains("No setting has been changed")))
                .Do("ask the org what was written", r =>
                    r.Check(Updates(r).Count == 0, Updates(r).Count + " updates were sent"));
        }

        private static Scenario ResetThenPickSendsTheValue()
        {
            return new Scenario
                {
                    Name = "reset-then-pick-sends-the-value",
                    Why = "fix/update-only-changes: a value equal to the clicked user's can still be sent, after Reset",
                    Org = () => FakeOrg.Sample("Org A", 3)
                }
                .Loaded(3)
                .Select(0)
                .Do("press Reset and pick the same paging limit", r =>
                {
                    r.Find<ToolStripButton>("tsbReset").PerformClick();
                    r.Find<ComboBox>("cbbPagingLimit").SelectedItem = "50";
                })
                .Update(1)
                .Finished()
                .Do("ask the org what was written", r =>
                {
                    var written = Updates(r).FirstOrDefault()?.Written;
                    r.Check(written?.GetAttributeValue<int>("paginglimit") == 50, "the paging limit was not written");
                });
        }

        private static Scenario AreaFromAnotherAppIsSent()
        {
            return new Scenario
                {
                    Name = "area-from-another-app-is-sent",
                    Why = "fix/update-only-changes: the same place in another app's list is a different area",
                    Org = () => FakeOrg.Sample("Org A", 3)
                }
                .Loaded(3)
                .Select(0)
                .Do("switch app and pick its first area", r =>
                {
                    var apps = r.Find<ToolStripComboBox>("applist");
                    apps.SelectedIndex = apps.SelectedIndex == 0 ? 1 : 0;
                    r.Find<ComboBox>("cbbSiteMapArea").SelectedIndex = 1;
                })
                .Update(1)
                .Finished()
                .Do("ask the org what was written", r =>
                {
                    var written = Updates(r).FirstOrDefault()?.Written;
                    var picked = r.Find<ComboBox>("cbbSiteMapArea").SelectedItem?.ToString();
                    r.Check(written?.GetAttributeValue<string>("homepagearea") == picked, "the area " + picked + " was not written");
                    var keys = written?.Attributes.Keys.Where(k => k != "usersettingsid").ToArray() ?? new string[0];
                    r.Check(keys.Length == 1, "wrote: " + string.Join(", ", keys));
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
