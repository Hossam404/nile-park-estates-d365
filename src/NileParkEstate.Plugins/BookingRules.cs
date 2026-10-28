using System;

namespace NileParkEstate.Plugins
{
    /// <summary>
    /// Pure booking rules with no Dataverse dependency, so they can be unit tested directly.
    /// </summary>
    public static class BookingRules
    {
        /// <summary>True when both times are set and the booking ends after it starts.</summary>
        public static bool EndsAfterStart(DateTime start, DateTime end)
        {
            return end > start;
        }

        /// <summary>
        /// Two bookings overlap when each one starts before the other ends.
        /// Back-to-back bookings (one ends exactly when the next starts) do not overlap.
        /// </summary>
        public static bool Overlaps(DateTime startA, DateTime endA, DateTime startB, DateTime endB)
        {
            return startA < endB && startB < endA;
        }

        /// <summary>
        /// Total price = exact duration in hours x hourly rate, less the member discount.
        /// The brief gives no rounding rule for partial hours, so the exact duration is used
        /// (90 minutes = 1.5 hours) and the result is rounded to 2 decimals.
        /// </summary>
        public static decimal CalculatePrice(DateTime start, DateTime end, decimal hourlyRate, decimal discountPercent)
        {
            if (!EndsAfterStart(start, end))
                throw new ArgumentException("End must be after start.");

            if (discountPercent < 0m) discountPercent = 0m;
            if (discountPercent > 100m) discountPercent = 100m;

            decimal hours = (decimal)(end - start).TotalMinutes / 60m;
            decimal gross = hours * hourlyRate;
            decimal net = gross * (1m - discountPercent / 100m);

            return Math.Round(net, 2, MidpointRounding.AwayFromZero);
        }
    }
}
