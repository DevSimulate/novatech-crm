namespace NovaTechCRM.Domain.ValueObjects;

public static class BusinessDays
{
    // 3 business days matches the contractual grace period before overdue notices may be sent.
    public const int OverdueGracePeriod = 3;

    // Returns the date after advancing by the given number of Mon–Fri business days.
    // Does not account for public holidays.
    public static DateTime Add(DateTime date, int days)
    {
        int added = 0;
        while (added < days)
        {
            date = date.AddDays(1);
            if (date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday)
                added++;
        }
        return date;
    }
}
