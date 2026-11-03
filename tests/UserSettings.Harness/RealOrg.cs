using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using MsCrmTools.UserSettingsUtility.UserControls;

namespace UserSettings.Harness
{
    /// <summary>
    /// The tool against a real Dataverse, changing exactly one user's settings: the signed-in
    /// user's own. It narrows the list to that user the way FetchXML Builder would, refuses to
    /// press Update with anyone else ticked, asks the org afterwards what was written, and puts
    /// the original values back whatever happened.
    /// </summary>
    public static class RealOrg
    {
        /// <summary>The borrowed sign in, as a connection. Never prompts.</summary>
        public static CrmServiceClient Connect(string url, string username, string cache)
        {
            return new CrmServiceClient(
                "AuthType=OAuth;Url=" + url
                + ";Username=" + username
                + ";AppId=51f81489-12ee-4a9e-aaae-a2591f45987d"
                + ";RedirectUri=app://58145B91-0C36-4500-8554-080854F2AC97"
                + ";TokenCacheStorePath=" + cache
                + ";LoginPrompt=Never;RequireNewInstance=True");
        }

        private static Entity ReadSettings(IOrganizationService service, Guid userId)
        {
            return service.RetrieveMultiple(new QueryByAttribute("usersettings")
            {
                Attributes = { "systemuserid" },
                Values = { userId },
                ColumnSet = new ColumnSet(true)
            }).Entities.Single();
        }

        /// <summary>Columns the org changes on every write, whoever writes.</summary>
        private static readonly HashSet<string> Bookkeeping = new HashSet<string>
        {
            "modifiedon", "modifiedby", "modifiedonbehalfby", "versionnumber", "usersettingsid", "systemuserid"
        };

        private static string Describe(object value)
        {
            switch (value)
            {
                case null: return "(null)";
                case OptionSetValue option: return option.Value.ToString();
                case EntityReference reference: return reference.Id.ToString();
                case Money money: return money.Value.ToString();
                default: return value.ToString();
            }
        }

        /// <summary>Every attribute whose value differs between two reads of the row.</summary>
        private static List<string> Changed(Entity before, Entity after)
        {
            return before.Attributes.Keys.Union(after.Attributes.Keys)
                .Where(k => !Bookkeeping.Contains(k))
                .Where(k => Describe(before.GetAttributeValue<object>(k)) != Describe(after.GetAttributeValue<object>(k)))
                .OrderBy(k => k)
                .ToList();
        }

        public static List<string> Play(CrmServiceClient client, string root)
        {
            var me = ((WhoAmIResponse)client.Execute(new WhoAmIRequest())).UserId;
            var before = ReadSettings(client, me);
            var paging = before.GetAttributeValue<int>("paginglimit") == 100 ? 75 : 100;
            // Not one of the list's half hours, so it goes through the typed path #51 was about
            var start = before.GetAttributeValue<string>("workdaystarttime") == "08:45" ? "08:15" : "08:45";

            Console.WriteLine("  signed in as " + me + "; paging " + before.GetAttributeValue<int>("paginglimit")
                              + " -> " + paging + ", start " + before.GetAttributeValue<string>("workdaystarttime")
                              + " -> " + start);

            var onlyMe = "<fetch><entity name='systemuser'><attribute name='fullname' /><attribute name='systemuserid' />"
                         + "<filter><condition attribute='systemuserid' operator='eq' value='" + me + "' /></filter>"
                         + "</entity></fetch>";

            var scenario = new Scenario
                {
                    Name = "real-org-own-settings",
                    Why = "the whole path against " + client.ConnectedOrgFriendlyName + ", on the signed-in user only",
                    Org = () => client
                }
                .Do("press Load", r => r.Find<ToolStripButton>("tsbLoadCrmItems").PerformClick())
                .WaitFor("the settings and the first view's users to arrive",
                    r => r.Find<Panel>("panel1").Enabled && r.Users.Items.Count > 0, 120000)
                .Do("narrow the list to me, the way FetchXML Builder does",
                    r => Ui.Field<UserSelector>(r.Control, "userSelector1").PopulateUsers(onlyMe))
                .WaitFor("only me in the list", r => r.Users.Items.Count == 1 && ((Entity)r.Users.Items[0].Tag).Id == me, 60000)
                .Do("select me", r =>
                {
                    r.Users.Items[0].Selected = true;
                    r.Users.FocusedItem = r.Users.Items[0];
                })
                .WaitFor("my settings to show",
                    r => r.Find<ComboBox>("cbbPagingLimit").Text == before.GetAttributeValue<int>("paginglimit").ToString(), 30000)
                .Do("choose the new values", r =>
                {
                    r.Find<ComboBox>("cbbPagingLimit").SelectedItem = paging.ToString();
                    r.Find<ComboBox>("cbbWorkStartTime").Text = start;
                })
                .Do("tick me, and only me", r =>
                {
                    r.Users.Items[0].Checked = true;
                    var ticked = Ui.Field<UserSelector>(r.Control, "userSelector1").SelectedItems;
                    if (ticked.Count != 1 || ticked[0].Id != me)
                        throw new InvalidOperationException("refusing to press Update with " + ticked.Count + " users ticked");
                })
                .Do("press Update", r => r.Find<ToolStripButton>("tsbUpdateUserSettings").PerformClick())
                .WaitFor("the update to finish", r => r.Dialogs.Any(d => d.Contains("Do you want to open log file?")), 120000)
                .Do("ask the org what was written", r =>
                {
                    var after = ReadSettings(client, me);
                    r.Check(after.GetAttributeValue<int>("paginglimit") == paging,
                        "the org's paging limit is " + after.GetAttributeValue<int>("paginglimit") + ", not " + paging);
                    r.Check(after.GetAttributeValue<string>("workdaystarttime") == start,
                        "the org's start time is " + after.GetAttributeValue<string>("workdaystarttime") + ", not " + start);
                    r.Check(!r.Dialogs.Any(d => d.StartsWith("Error")), "the tool reported an error: "
                        + string.Join(" | ", r.Dialogs.Where(d => d.StartsWith("Error"))));

                    // The form shows every one of the user's settings and Update writes them all
                    // back, so a value that does not survive the trip through a combo box is a
                    // setting changed behind the user's back
                    var unasked = Changed(before, after).Except(new[] { "paginglimit", "workdaystarttime" }).ToList();
                    r.Check(unasked.Count == 0, "Update also changed what nobody chose: " + string.Join(", ",
                        unasked.Select(k => k + " " + Describe(before.GetAttributeValue<object>(k)) + " -> "
                                            + Describe(after.GetAttributeValue<object>(k)))));
                });

            try
            {
                return scenario.Play(root);
            }
            finally
            {
                // Put back every attribute that differs, as the user, whatever the run did
                client.CallerId = Guid.Empty;
                var changed = Changed(before, ReadSettings(client, me));
                if (changed.Count > 0)
                {
                    var restore = new Entity("usersettings", before.Id);
                    foreach (var k in changed) restore[k] = before.Contains(k) ? before[k] : null;
                    client.Update(restore);
                }

                var left = Changed(before, ReadSettings(client, me));
                Console.WriteLine("  restored " + changed.Count + " attributes ("
                                  + string.Join(", ", changed) + ")"
                                  + (left.Count == 0 ? "; the row matches what was there" : "; STILL DIFFERENT: " + string.Join(", ", left)));
            }
        }
    }
}
