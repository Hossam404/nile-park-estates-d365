using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace NileParkEstate.Plugins
{
    /// <summary>
    /// Validates and prices atos_booking records on the server, so the rules hold for the form,
    /// data imports and direct API calls alike, and cannot be bypassed from the browser.
    ///
    /// Registration (all synchronous, server only):
    ///   Pre-operation (validation + pricing):
    ///   - Create of atos_booking
    ///   - Update of atos_booking, filtering attributes:
    ///       atos_starttime, atos_endtime, atos_facilityid, atos_memberid, atos_totalprice, statuscode, statecode
    ///     with a Pre Image named "PreImage" containing:
    ///       atos_starttime, atos_endtime, atos_facilityid, atos_memberid, statuscode, ownerid
    ///   Post-operation (location ownership):
    ///   - Create of atos_booking
    ///   - Update of atos_booking, filtering attribute: atos_facilityid (no image needed)
    ///
    /// What it does:
    ///   1. Rejects a booking whose End Time is not after its Start Time.
    ///   2. Rejects a booking that overlaps another non-cancelled booking for the same facility.
    ///   3. Sets Total Price = duration (hours) x facility hourly rate, less the member's discount
    ///      when the member is a contact with an active membership. Any value typed by the user is overwritten.
    ///   4. (Post-operation) Sets each booking's location (owning business unit) to its facility's,
    ///      so row-level security by location holds however the booking was created.
    ///      Requires the environment setting "Record ownership across business units".
    /// </summary>
    public class BookingValidationAndPricing : IPlugin
    {
        private const string PreImageName = "PreImage";
        private const int StagePostOperation = 40;

        // atos_booking
        private const string BookingEntity = "atos_booking";
        private const string BookingId = "atos_bookingid";
        private const string BookingName = "atos_name";
        private const string StartTime = "atos_starttime";
        private const string EndTime = "atos_endtime";
        private const string Facility = "atos_facilityid";
        private const string Member = "atos_memberid"; // Contact-only lookup (members are people)
        private const string TotalPrice = "atos_totalprice";
        private const string StatusReason = "statuscode";
        private const int StatusReasonCancelled = 2;

        // atos_facility
        private const string FacilityEntity = "atos_facility";
        private const string FacilityCode = "atos_name";
        private const string HourlyRate = "atos_hourlyrate";
        private const string CurrentStatus = "atos_currentstatus";
        private const int StatusUnderMaintenance = 496710001;

        // contact (members)
        private const string ContactEntity = "contact";
        private const string HasActiveMembership = "atos_hasactivemembership";
        private const string MembershipDiscountPercent = "atos_membershipdiscountpercent";

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            if (!context.InputParameters.Contains("Target") || !(context.InputParameters["Target"] is Entity target))
                return;
            if (target.LogicalName != BookingEntity)
                return;

            // System context on purpose: the overlap check must see every booking for the facility,
            // including ones the current user (e.g. a Coordinator limited to their own location) cannot read.
            IOrganizationService service = factory.CreateOrganizationService(null);

            // Use the image named "PreImage"; fall back to whatever single pre-image is registered.
            Entity preImage = context.PreEntityImages.Contains(PreImageName)
                ? context.PreEntityImages[PreImageName]
                : (context.PreEntityImages.Count > 0 ? context.PreEntityImages.Values.First() : null);
            Entity booking = MergeWithPreImage(target, preImage);

            // Post-operation step: only keeps the booking in its facility's location.
            // This must run after the save: changing the owner inside Pre-operation of a Create does not
            // move the record's owning business unit, while a real assignment afterwards does.
            if (context.Stage == StagePostOperation)
            {
                EntityReference facilityRef = booking.GetAttributeValue<EntityReference>(Facility);
                if (facilityRef != null && (context.MessageName == "Create" || target.Contains(Facility)))
                {
                    EnsureOwnedInFacilityLocation(service, tracing, context, booking, context.PrimaryEntityId, facilityRef.Id);
                }
                return;
            }

            DateTime? start = booking.GetAttributeValue<DateTime?>(StartTime);
            DateTime? end = booking.GetAttributeValue<DateTime?>(EndTime);
            EntityReference facility = booking.GetAttributeValue<EntityReference>(Facility);
            EntityReference member = booking.GetAttributeValue<EntityReference>(Member);
            int? statusReason = booking.GetAttributeValue<OptionSetValue>(StatusReason)?.Value;

            tracing.Trace("Booking {0} ({1}): start={2:o} end={3:o} facility={4} member={5} status={6}",
                target.Id, context.MessageName, start, end, facility?.Id, member?.Id, statusReason);

            // 1. End must be after start.
            if (start.HasValue && end.HasValue && !BookingRules.EndsAfterStart(start.Value, end.Value))
            {
                throw new InvalidPluginExecutionException("The booking's End Time must be after its Start Time.");
            }

            bool isCancelled = statusReason == StatusReasonCancelled;

            // 2a. A facility under maintenance cannot be booked. Checked when a booking is created or when its
            //     facility, times or status change, so unrelated edits to existing bookings are not blocked.
            bool bookingChanged = context.MessageName == "Create" || target.Contains(Facility) || target.Contains(StartTime)
                                  || target.Contains(EndTime) || target.Contains(StatusReason);
            if (!isCancelled && facility != null && bookingChanged)
            {
                var facilityRow = service.Retrieve(FacilityEntity, facility.Id, new ColumnSet(FacilityCode, CurrentStatus));
                if (facilityRow.GetAttributeValue<OptionSetValue>(CurrentStatus)?.Value == StatusUnderMaintenance)
                {
                    throw new InvalidPluginExecutionException(
                        $"Facility {facilityRow.GetAttributeValue<string>(FacilityCode)} is under maintenance and can't be booked " +
                        "until its maintenance case is resolved. Choose another facility.");
                }
            }

            // 2b. No overlapping bookings for the same facility, unless this booking is cancelled.
            if (!isCancelled && facility != null && start.HasValue && end.HasValue)
            {
                Entity clash = FindOverlappingBooking(service, target.Id, facility.Id, start.Value, end.Value);
                if (clash != null)
                {
                    throw new InvalidPluginExecutionException(BuildOverlapMessage(service, context.InitiatingUserId, facility, clash));
                }
            }

            // 3. Price. The system owns this value; whatever the user typed is replaced.
            if (facility != null && start.HasValue && end.HasValue)
            {
                decimal rate = GetHourlyRate(service, facility.Id);
                decimal discount = GetMemberDiscountPercent(service, member);
                decimal price = BookingRules.CalculatePrice(start.Value, end.Value, rate, discount);

                tracing.Trace("Pricing: rate={0} discount={1}% price={2}", rate, discount, price);
                target[TotalPrice] = new Money(price);
            }
            else
            {
                // Not enough information to price (e.g. a draft created from a won Opportunity).
                target[TotalPrice] = null;
            }
        }

        private static Entity MergeWithPreImage(Entity target, Entity preImage)
        {
            var merged = new Entity(target.LogicalName, target.Id);
            if (preImage != null)
            {
                foreach (var attribute in preImage.Attributes)
                    merged[attribute.Key] = attribute.Value;
            }
            foreach (var attribute in target.Attributes)
                merged[attribute.Key] = attribute.Value;
            return merged;
        }

        /// <summary>
        /// Puts the booking in its facility's location (business unit) while keeping its owner.
        /// Relies on the environment setting "Record ownership across business units" being on,
        /// which lets a record's owning business unit differ from its owner's business unit.
        /// Done after the save (same transaction) so it also works on Create.
        /// </summary>
        private static void EnsureOwnedInFacilityLocation(IOrganizationService service, ITracingService tracing,
            IPluginExecutionContext context, Entity booking, Guid bookingId, Guid facilityId)
        {
            var facilityBu = service.Retrieve(FacilityEntity, facilityId, new ColumnSet("owningbusinessunit"))
                                    .GetAttributeValue<EntityReference>("owningbusinessunit");
            if (facilityBu == null)
                return;

            var bookingBu = service.Retrieve(BookingEntity, bookingId, new ColumnSet("owningbusinessunit"))
                                   .GetAttributeValue<EntityReference>("owningbusinessunit");
            if (bookingBu != null && bookingBu.Id == facilityBu.Id)
                return;

            tracing.Trace("Moving booking {0} to location {1}", bookingId, facilityBu.Id);

            // owningbusinessunit is not a filtering attribute of the Pre-operation Update step,
            // so this does not re-run validation or pricing.
            var move = new Entity(BookingEntity, bookingId);
            move["owningbusinessunit"] = new EntityReference("businessunit", facilityBu.Id);
            service.Update(move);
        }

        private static Entity FindOverlappingBooking(IOrganizationService service, Guid bookingId, Guid facilityId, DateTime start, DateTime end)
        {
            // Overlap: other.start < this.end AND other.end > this.start
            var query = new QueryExpression(BookingEntity)
            {
                ColumnSet = new ColumnSet(BookingName, StartTime, EndTime),
                TopCount = 1,
                NoLock = false
            };
            query.Criteria.AddCondition(Facility, ConditionOperator.Equal, facilityId);
            query.Criteria.AddCondition(StatusReason, ConditionOperator.NotEqual, StatusReasonCancelled);
            query.Criteria.AddCondition(StartTime, ConditionOperator.LessThan, end);
            query.Criteria.AddCondition(EndTime, ConditionOperator.GreaterThan, start);
            if (bookingId != Guid.Empty)
                query.Criteria.AddCondition(BookingId, ConditionOperator.NotEqual, bookingId);

            var results = service.RetrieveMultiple(query);
            return results.Entities.Count > 0 ? results.Entities[0] : null;
        }

        private static string BuildOverlapMessage(IOrganizationService service, Guid userId, EntityReference facility, Entity clash)
        {
            string facilityName = facility.Name;
            if (string.IsNullOrEmpty(facilityName))
            {
                var f = service.Retrieve(FacilityEntity, facility.Id, new ColumnSet(FacilityCode));
                facilityName = f.GetAttributeValue<string>(FacilityCode);
            }

            string from = FormatForUser(service, userId, clash.GetAttributeValue<DateTime>(StartTime));
            string to = FormatForUser(service, userId, clash.GetAttributeValue<DateTime>(EndTime));
            string clashName = clash.GetAttributeValue<string>(BookingName);
            string reference = string.IsNullOrEmpty(clashName) ? string.Empty : $" (booking \"{clashName}\")";

            return $"Facility {facilityName} is already booked from {from} to {to}{reference}. " +
                   "Choose a different time or facility, or cancel the other booking first.";
        }

        /// <summary>Shows a UTC time in the user's own time zone; falls back to UTC if that fails.</summary>
        private static string FormatForUser(IOrganizationService service, Guid userId, DateTime utc)
        {
            try
            {
                var settings = service.Retrieve("usersettings", userId, new ColumnSet("timezonecode"));
                int? timeZoneCode = settings.GetAttributeValue<int?>("timezonecode");
                if (timeZoneCode.HasValue)
                {
                    var request = new OrganizationRequest("LocalTimeFromUtcTime");
                    request["TimeZoneCode"] = timeZoneCode.Value;
                    request["UtcTime"] = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                    var response = service.Execute(request);
                    var local = (DateTime)response["LocalTime"];
                    return local.ToString("dd/MM/yyyy HH:mm");
                }
            }
            catch (Exception)
            {
                // Formatting is cosmetic; never block the real error message because of it.
            }
            return utc.ToString("dd/MM/yyyy HH:mm") + " UTC";
        }

        private static decimal GetHourlyRate(IOrganizationService service, Guid facilityId)
        {
            var facility = service.Retrieve(FacilityEntity, facilityId, new ColumnSet(FacilityCode, HourlyRate));
            var rate = facility.GetAttributeValue<Money>(HourlyRate);
            if (rate == null)
            {
                throw new InvalidPluginExecutionException(
                    $"Facility {facility.GetAttributeValue<string>(FacilityCode)} has no Hourly Rate, so the booking cannot be priced. Set the facility's Hourly Rate first.");
            }
            return rate.Value;
        }

        /// <summary>Discount applies only to contacts with an active membership.</summary>
        private static decimal GetMemberDiscountPercent(IOrganizationService service, EntityReference member)
        {
            if (member == null || member.LogicalName != ContactEntity)
                return 0m;

            var contact = service.Retrieve(ContactEntity, member.Id, new ColumnSet(HasActiveMembership, MembershipDiscountPercent));
            if (!contact.GetAttributeValue<bool>(HasActiveMembership))
                return 0m;

            return contact.GetAttributeValue<decimal?>(MembershipDiscountPercent) ?? 0m;
        }
    }
}