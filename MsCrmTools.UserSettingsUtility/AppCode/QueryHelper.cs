using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Xml.XPath;

namespace MsCrmTools.UserSettingsUtility.AppCode
{
    internal class QueryHelper
    {
        private const int UsersPerSettingsQuery = 500;

        public static EntityCollection GetItems(string fetchXml, IOrganizationService service)
        {
            // Check if businessunitid attribute is contained in attriburtes
            var xDoc = XDocument.Parse(fetchXml);
            AddMissingCrmAttribute(xDoc, "businessunitid");

            var entityElement = xDoc.Descendants("entity").FirstOrDefault();
            if (entityElement == null)
            {
                throw new Exception("Cannot find node 'entity' in FetchXml");
            }

            AddMissingCrmAttribute(xDoc, "firstname");
            AddMissingCrmAttribute(xDoc, "lastname");
            AddMissingCrmAttribute(xDoc, "fullname");

            var response = (FetchXmlToQueryExpressionResponse)service.Execute(new FetchXmlToQueryExpressionRequest
            {
                FetchXml = xDoc.ToString()
            });

            var query = response.Query;
            query.PageInfo = new PagingInfo
            {
                PageNumber = 1,
                Count = 1000
            };
            var ec = new EntityCollection();
            EntityCollection result;
            do
            {
                result = service.RetrieveMultiple(query);
                ec.Entities.AddRange(result.Entities);
                ec.EntityName = result.EntityName;

                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = result.PagingCookie;
            } while (result.MoreRecords);

            return ec;
        }

        public static Dictionary<Guid, Entity> GetUserSettings(List<Guid> userIds, IOrganizationService service)
        {
            var settings = new Dictionary<Guid, Entity>();
            for (var i = 0; i < userIds.Count; i += UsersPerSettingsQuery)
            {
                var ids = userIds.Skip(i).Take(UsersPerSettingsQuery).Cast<object>().ToArray();

                // Every column, as a clicked user's read does: an older org lacks some of the
                // settings this tool knows, and naming one it lacks would fail the whole query
                var result = service.RetrieveMultiple(new QueryExpression("usersettings")
                {
                    ColumnSet = new ColumnSet(true),
                    Criteria = { Conditions = { new ConditionExpression("systemuserid", ConditionOperator.In, ids) } }
                });

                foreach (var row in result.Entities)
                {
                    settings[row.Id] = row;
                }
            }

            return settings;
        }

        private static void AddMissingCrmAttribute(XDocument xDoc, string attributeName)
        {
            var xBuAttribute = xDoc.XPathSelectElement("fetch/entity/attribute[@name='" + attributeName + "']");
            if (xBuAttribute == null)
            {
                var entityElement = xDoc.Descendants("entity").FirstOrDefault();
                if (entityElement == null)
                {
                    throw new Exception("Cannot find node 'entity' in FetchXml");
                }

                entityElement.Add(new XElement("attribute", new XAttribute("name", attributeName)));
            }
        }
    }
}