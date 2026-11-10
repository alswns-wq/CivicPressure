#if DEV
using System.Globalization;

namespace CivicPressure
{
    // Explicit allow-list. Never serialize ComplaintView, CityView, credentials or settings.
    internal static class DevAiInput
    {
        internal static string Encode(string district, string service, int affected, int severe, int population)
        {
            if (string.IsNullOrEmpty(district) || string.IsNullOrEmpty(service)) return "";
            return "{\n  \"district\": " + LocaleFile.Quote(district) +
                ",\n  \"complaintType\": " + LocaleFile.Quote(service) +
                ",\n  \"serviceState\": {\n    \"affected\": " + affected.ToString(CultureInfo.InvariantCulture) +
                ",\n    \"severe\": " + severe.ToString(CultureInfo.InvariantCulture) +
                ",\n    \"population\": " + population.ToString(CultureInfo.InvariantCulture) + "\n  }\n}";
        }
    }
}
#endif
