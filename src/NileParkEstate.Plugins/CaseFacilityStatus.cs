using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace NileParkEstate.Plugins
{
    /// <summary>
    /// Keeps a facility's Current Status in line with its maintenance cases, in the same transaction as the case.
    ///
    /// Registration (all Post-operation, synchronous, server only, primary entity incident):
    ///   - Create of incident
    ///   - Update of incident, filtering attributes: atos_facilityid, statecode
    ///     with a Pre Image named "PreImage" containing: atos_facilityid
    ///   - Close of incident            (resolving a case)
    ///
    /// What it does:
    ///   1. A new case with a facility is routed to the "Maintenance" queue.
    ///   2. The facility is "Under Maintenance" while it has at least one active case,
    ///      and goes back to "Available" when its last active case is resolved or cancelled.
    ///      Recalculating from the open cases (rather than flipping on each event) handles
    ///      several cases on one facility, reactivated cases and a case moved to another facility.
    /// </summary>
    public class CaseFacilityStatus : IPlugin
    {
        private const string CaseEntity = "incident";
        private const string CaseFacility = "atos_facilityid";
        private const int CaseStateActive = 0;

        private const string FacilityEntity = "atos_facility";
        private const string CurrentStatus = "atos_currentstatus";
        private const int StatusAvailable = 496710000;
        private const int StatusUnderMaintenance = 496710001;

        private const string MaintenanceQueueName = "Maintenance";

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            // System context: staff raising a case may not be allowed to edit the facility itself.
            IOrganizationService service = factory.CreateOrganizationService(null);

            Guid caseId = GetCaseId(context);
            if (caseId == Guid.Empty)
                return;

            // Facilities whose status may have changed: the case's current facility, plus its previous one if it moved.
            var current = service.Retrieve(CaseEntity, caseId, new ColumnSet(CaseFacility, "statecode"));
            EntityReference facility = current.GetAttributeValue<EntityReference>(CaseFacility);

            Entity preImage = context.PreEntityImages.Count > 0 ? context.PreEntityImages.Values.First() : null;
            EntityReference previousFacility = preImage?.GetAttributeValue<EntityReference>(CaseFacility);

            tracing.Trace("Case {0} ({1}): facility={2} previous={3}", caseId, context.MessageName, facility?.Id, previousFacility?.Id);

            if (context.MessageName == "Create" && facility != null)
            {
                RouteToMaintenanceQueue(service, tracing, caseId);
            }

            if (facility != null)
                RecalculateFacilityStatus(service, tracing, facility.Id);

            if (previousFacility != null && (facility == null || previousFacility.Id != facility.Id))
                RecalculateFacilityStatus(service, tracing, previousFacility.Id);
        }

        private static Guid GetCaseId(IPluginExecutionContext context)
        {
            // Close (resolve): the target is the IncidentResolution activity, which points at the case.
            if (context.MessageName == "Close" && context.InputParameters.Contains("IncidentResolution")
                && context.InputParameters["IncidentResolution"] is Entity resolution)
            {
                return resolution.GetAttributeValue<EntityReference>("incidentid")?.Id ?? Guid.Empty;
            }

            if (context.InputParameters.Contains("Target"))
            {
                if (context.InputParameters["Target"] is Entity target && target.LogicalName == CaseEntity)
                    return target.Id != Guid.Empty ? target.Id : context.PrimaryEntityId;
                if (context.InputParameters["Target"] is EntityReference reference && reference.LogicalName == CaseEntity)
                    return reference.Id;
            }

            return context.PrimaryEntityName == CaseEntity ? context.PrimaryEntityId : Guid.Empty;
        }

        /// <summary>Under Maintenance while any active case exists for the facility, otherwise Available.</summary>
        private static void RecalculateFacilityStatus(IOrganizationService service, ITracingService tracing, Guid facilityId)
        {
            var openCases = new QueryExpression(CaseEntity) { ColumnSet = new ColumnSet(false), TopCount = 1 };
            openCases.Criteria.AddCondition(CaseFacility, ConditionOperator.Equal, facilityId);
            openCases.Criteria.AddCondition("statecode", ConditionOperator.Equal, CaseStateActive);
            bool underMaintenance = service.RetrieveMultiple(openCases).Entities.Count > 0;

            int wanted = underMaintenance ? StatusUnderMaintenance : StatusAvailable;
            var facility = service.Retrieve(FacilityEntity, facilityId, new ColumnSet(CurrentStatus));
            int? currentStatus = facility.GetAttributeValue<OptionSetValue>(CurrentStatus)?.Value;
            if (currentStatus == wanted)
                return;

            tracing.Trace("Facility {0}: {1} -> {2}", facilityId, currentStatus, wanted);
            var update = new Entity(FacilityEntity, facilityId);
            update[CurrentStatus] = new OptionSetValue(wanted);
            service.Update(update);
        }

        private static void RouteToMaintenanceQueue(IOrganizationService service, ITracingService tracing, Guid caseId)
        {
            var queueQuery = new QueryExpression("queue") { ColumnSet = new ColumnSet("name"), TopCount = 1 };
            queueQuery.Criteria.AddCondition("name", ConditionOperator.Equal, MaintenanceQueueName);
            var queue = service.RetrieveMultiple(queueQuery).Entities.FirstOrDefault();
            if (queue == null)
            {
                tracing.Trace("Queue '{0}' not found; case not routed.", MaintenanceQueueName);
                return;
            }

            var request = new OrganizationRequest("AddToQueue");
            request["Target"] = new EntityReference(CaseEntity, caseId);
            request["DestinationQueueId"] = queue.Id;
            service.Execute(request);
            tracing.Trace("Case {0} added to queue {1}", caseId, queue.Id);
        }
    }
}