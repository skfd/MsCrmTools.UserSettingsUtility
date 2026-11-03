using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace UserSettings.Harness
{
    /// <summary>One request the tool made, and where it made it from.</summary>
    public class Call
    {
        public string What;
        public int ThreadId;
        public Guid CallerId;
        public Entity Written;
    }

    /// <summary>
    /// An organization that exists only in memory, answering exactly the requests this tool makes
    /// and nothing else. A request it does not know is a finding, not a gap to paper over: it
    /// throws, and the scenario hears about it.
    ///
    /// CallerId is here because the tool impersonates each user it updates by reflecting on a
    /// property of that name, the way CrmServiceClient exposes it. Every call records who it was
    /// made as and on which thread, which is what the scenarios assert on.
    /// </summary>
    public class FakeOrg : IOrganizationService
    {
        public static readonly Guid DefaultSiteMapId = new Guid("00000000-0000-0000-0000-00000000a001");
        public static readonly Guid AppSiteMapId = new Guid("00000000-0000-0000-0000-00000000a002");

        public readonly string Name;
        public Guid CallerId { get; set; }

        /// <summary>How long a usersettings read takes, so that waiting on one shows.</summary>
        public int SettingsLatencyMs;

        public readonly List<Entity> Users = new List<Entity>();
        public readonly Dictionary<Guid, Entity> Settings = new Dictionary<Guid, Entity>();
        public readonly List<Entity> SiteMaps = new List<Entity>();
        public readonly List<Entity> AppComponents = new List<Entity>();

        private readonly List<Call> _calls = new List<Call>();

        public FakeOrg(string name)
        {
            Name = name;
        }

        public List<Call> Calls
        {
            get { lock (_calls) return _calls.ToList(); }
        }

        public void ForgetCalls()
        {
            lock (_calls) _calls.Clear();
        }

        /// <summary>
        /// A small org: a default sitemap and one app with a sitemap of its own, and as many
        /// users as asked for, each with a full usersettings row.
        /// </summary>
        public static FakeOrg Sample(string name, int users, string areaPrefix = "")
        {
            var org = new FakeOrg(name);

            org.SiteMaps.Add(SiteMap(DefaultSiteMapId, "SiteMap",
                Area(areaPrefix + "Workplace", areaPrefix + "nav_dashboards", areaPrefix + "nav_activities"),
                Area(areaPrefix + "SFA", areaPrefix + "nav_accts", areaPrefix + "nav_conts")));
            org.SiteMaps.Add(SiteMap(AppSiteMapId, "contoso_hub",
                Area(areaPrefix + "HubArea", areaPrefix + "hub_cases")));

            org.AppComponents.Add(new Entity("appmodulecomponent", Guid.NewGuid())
            {
                ["componenttype"] = new OptionSetValue(62),
                ["objectid"] = AppSiteMapId,
                ["appmoduleidunique"] = new EntityReference("appmodule", Guid.NewGuid()) { Name = "Contoso Hub" }
            });

            var bu = new EntityReference("businessunit", Guid.NewGuid()) { Name = name };
            for (var i = 1; i <= users; i++)
            {
                var id = new Guid(i, 0, 0, new byte[8]);
                org.Users.Add(new Entity("systemuser", id)
                {
                    ["systemuserid"] = id,
                    ["firstname"] = "User",
                    ["lastname"] = name + " " + i.ToString("00"),
                    ["fullname"] = "User " + name + " " + i.ToString("00"),
                    ["businessunitid"] = bu
                });
                org.Settings[id] = DefaultSettings(id);
            }

            return org;
        }

        /// <summary>
        /// Every attribute the tool reads when it shows a user, with values a fresh org gives.
        /// Several of them it reads with .Value or .Single, so a row missing one would throw for a
        /// reason no real org produces.
        /// </summary>
        public static Entity DefaultSettings(Guid userId)
        {
            return new Entity("usersettings", userId)
            {
                ["systemuserid"] = userId,
                ["homepagearea"] = "Workplace",
                ["homepagesubarea"] = "nav_dashboards",
                ["advancedfindstartupmode"] = 1,
                ["autocreatecontactonpromote"] = 1,
                ["defaultcalendarview"] = 0,
                ["incomingemailfilteringmethod"] = new OptionSetValue(1),
                ["reportscripterrors"] = new OptionSetValue(1),
                ["paginglimit"] = 50,
                ["timezonecode"] = 35,
                ["workdaystarttime"] = "08:00",
                ["workdaystoptime"] = "17:00",
                ["helplanguageid"] = 1033,
                ["uilanguageid"] = 1033,
                ["localeid"] = 1033
            };
        }

        private static XElement Area(string id, params string[] subAreas)
        {
            return new XElement("Area", new XAttribute("Id", id),
                new XElement("Group", new XAttribute("Id", id + "_group"),
                    subAreas.Select(s => new XElement("SubArea", new XAttribute("Id", s)))));
        }

        private static Entity SiteMap(Guid id, string sitemapName, params XElement[] areas)
        {
            return new Entity("sitemap", id)
            {
                ["sitemapid"] = id,
                ["sitemapname"] = sitemapName,
                ["sitemapxml"] = new XElement("SiteMap", areas).ToString(SaveOptions.DisableFormatting),
                ["componentstate"] = new OptionSetValue(0)
            };
        }

        private void Record(string what, Entity written = null)
        {
            lock (_calls)
            {
                _calls.Add(new Call
                {
                    What = what,
                    ThreadId = Thread.CurrentThread.ManagedThreadId,
                    CallerId = CallerId,
                    Written = written
                });
            }
        }

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            Record("Execute " + request.RequestName);

            switch (request)
            {
                case RetrieveProvisionedLanguagesRequest _:
                    return new RetrieveProvisionedLanguagesResponse
                    {
                        Results = { ["RetrieveProvisionedLanguages"] = new[] { 1033 } }
                    };

                case GetAllTimeZonesWithDisplayNameRequest _:
                    var zones = new EntityCollection { EntityName = "timezonedefinition" };
                    zones.Entities.Add(new Entity("timezonedefinition", Guid.NewGuid())
                    {
                        ["timezonecode"] = 35,
                        ["userinterfacename"] = "(GMT-05:00) Eastern Time (US & Canada)"
                    });
                    zones.Entities.Add(new Entity("timezonedefinition", Guid.NewGuid())
                    {
                        ["timezonecode"] = 85,
                        ["userinterfacename"] = "(GMT) Coordinated Universal Time"
                    });
                    return new GetAllTimeZonesWithDisplayNameResponse { Results = { ["EntityCollection"] = zones } };

                case RetrieveVersionRequest _:
                    return new RetrieveVersionResponse { Results = { ["Version"] = "9.2.25101.100" } };

                case FetchXmlToQueryExpressionRequest fetch:
                    var entity = XDocument.Parse(fetch.FetchXml).Descendants("entity").First().Attribute("name").Value;
                    return new FetchXmlToQueryExpressionResponse
                    {
                        Results = { ["Query"] = new QueryExpression(entity) { ColumnSet = new ColumnSet(true) } }
                    };
            }

            throw new NotSupportedException(Name + " does not know the request " + request.RequestName);
        }

        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            switch (query)
            {
                case FetchExpression fetch:
                    var entity = XDocument.Parse(fetch.Query).Descendants("entity").First().Attribute("name").Value;
                    Record("RetrieveMultiple " + entity);
                    if (entity == "transactioncurrency" || entity == "systemform")
                        return new EntityCollection { EntityName = entity };
                    break;

                case QueryByAttribute byAttribute when byAttribute.EntityName == "usersettings":
                    var userId = (Guid)byAttribute.Values[0];
                    Record("RetrieveMultiple usersettings " + userId);
                    if (SettingsLatencyMs > 0) Thread.Sleep(SettingsLatencyMs);
                    var found = new EntityCollection { EntityName = "usersettings" };
                    lock (Settings)
                    {
                        if (Settings.TryGetValue(userId, out var row)) found.Entities.Add(Project(row, byAttribute.ColumnSet));
                    }
                    return found;

                case QueryExpression expression:
                    Record("RetrieveMultiple " + expression.EntityName);
                    switch (expression.EntityName)
                    {
                        case "sitemap":
                            return Collection("sitemap", SiteMaps.Select(Copy));
                        case "appmodulecomponent":
                            return Collection("appmodulecomponent", AppComponents.Select(Copy));
                        case "savedquery":
                            return Collection("savedquery", new[]
                            {
                                new Entity("savedquery", Guid.NewGuid())
                                {
                                    ["name"] = "Enabled Users",
                                    ["fetchxml"] = "<fetch><entity name='systemuser'><attribute name='fullname' /></entity></fetch>"
                                }
                            });
                        case "userquery":
                            return Collection("userquery", Enumerable.Empty<Entity>());
                        case "organization":
                            return Collection("organization", new[] { new Entity("organization", Guid.NewGuid()) });
                        case "systemuser":
                            return Collection("systemuser", Users.Select(Copy));
                    }
                    break;
            }

            throw new NotSupportedException(Name + " does not know the query " + query.GetType().Name);
        }

        public void Update(Entity entity)
        {
            Record("Update " + entity.LogicalName, Copy(entity));

            if (entity.LogicalName != "usersettings")
                throw new NotSupportedException(Name + " does not update " + entity.LogicalName);

            lock (Settings)
            {
                if (!Settings.TryGetValue(entity.Id, out var row))
                    throw new InvalidOperationException("usersettings " + entity.Id + " does not exist in " + Name);
                foreach (var attribute in entity.Attributes) row[attribute.Key] = attribute.Value;
            }
        }

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
        {
            Record("Retrieve " + entityName);
            if (entityName == "sitemap") return Copy(SiteMaps.Single(s => s.Id == id));
            throw new NotSupportedException(Name + " does not retrieve " + entityName);
        }

        private static EntityCollection Collection(string name, IEnumerable<Entity> entities)
        {
            var collection = new EntityCollection { EntityName = name };
            collection.Entities.AddRange(entities);
            return collection;
        }

        /// <summary>
        /// Only the columns asked for, the way an org answers. The update reads a row with no
        /// columns and writes it back with the changes on it, so returning everything here would
        /// make every update look like it rewrote the whole row.
        /// </summary>
        private static Entity Project(Entity source, ColumnSet columns)
        {
            if (columns != null && columns.AllColumns) return Copy(source);
            // An org always returns the primary key as an attribute, and the update counts on it:
            // it writes only when the row has more attributes than that one
            var copy = new Entity(source.LogicalName, source.Id) { [source.LogicalName + "id"] = source.Id };
            foreach (var name in columns?.Columns ?? Enumerable.Empty<string>())
                if (source.Contains(name)) copy[name] = source[name];
            return copy;
        }

        /// <summary>
        /// A copy, so the tool cannot change the org by changing what it was handed - which is
        /// what a real org's answer is like too.
        /// </summary>
        private static Entity Copy(Entity source)
        {
            var copy = new Entity(source.LogicalName, source.Id);
            foreach (var attribute in source.Attributes) copy[attribute.Key] = attribute.Value;
            return copy;
        }

        public Guid Create(Entity entity) { throw new NotSupportedException("Create " + entity.LogicalName); }
        public void Delete(string entityName, Guid id) { throw new NotSupportedException("Delete " + entityName); }
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { throw new NotSupportedException("Associate"); }
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { throw new NotSupportedException("Disassociate"); }
    }
}
