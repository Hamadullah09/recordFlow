using RecordFlow.Core.Entities;
using RecordFlow.Core.Workspaces;

namespace RecordFlow.Core.Services;

/// <summary>
/// Builds the editable form for a working record from the configured field definitions and the
/// record's CSV values. Missing CSV columns simply produce empty fields; CSV columns that match no
/// definition are kept as editable "Additional Information" fields.
/// </summary>
public static class FormBuilder
{
    public const string AdditionalFieldPrefix = "csv_";
    public const string BillingFieldPrefix = "billing_";

    public const string BillingName = "billing_name";
    public const string BillingEmail = "billing_email";
    public const string BillingAddress1 = "billing_address1";
    public const string BillingAddress2 = "billing_address2";
    public const string BillingCity = "billing_city";
    public const string BillingState = "billing_state";
    public const string BillingZip = "billing_zip";
    public const string BillingMethod = "billing_method";

    /// <summary>Payment method choices shown to the recipient (stored as the display text).</summary>
    public static readonly IReadOnlyList<PaymentMethodKind> PaymentMethods = [PaymentMethodKind.Card, PaymentMethodKind.BankAccount];

    public static PaymentMethodKind? ParsePaymentMethod(string? value) =>
        PaymentMethods.Cast<PaymentMethodKind?>().FirstOrDefault(m => string.Equals(m!.Value.Name(), value, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Billing fields added to every form. They travel to the recipient with the rest of the form so the store can
    /// fill in or correct them; the order is created from them only after the owner confirms. Card numbers are
    /// never part of the form – they are entered on the payment provider's hosted page.
    /// </summary>
    private static readonly (string Key, string Label, FieldType Type, bool Required, int MaxLength, string? PrefillFrom)[] BillingDefinitions =
    [
        (BillingName, "Name on billing", FieldType.Text, true, 100, "OwnerName"),
        (BillingEmail, "Billing email", FieldType.Email, true, 254, "OwnerEmail"),
        (BillingAddress1, "Billing street address", FieldType.Text, true, 200, "AddressLine1"),
        (BillingAddress2, "Apt, suite, etc.", FieldType.Text, false, 200, "AddressLine2"),
        (BillingCity, "Billing city", FieldType.Text, true, 100, "City"),
        (BillingState, "Billing state", FieldType.State, true, 0, "State"),
        (BillingZip, "Billing ZIP code", FieldType.ZipCode, true, 10, "ZipCode"),
    ];

    public static List<WorkingField> Build(
        IEnumerable<FormFieldDefinition> definitions,
        WorkingRecord record,
        IReadOnlyList<string> headers,
        string? contactIdHeader,
        bool offerBankAccount = false)
    {
        var fields = new List<WorkingField>();
        var consumedHeaders = new HashSet<string>(StringComparer.Ordinal);
        if (contactIdHeader is not null) consumedHeaders.Add(contactIdHeader);

        var headerLookup = headers
            .GroupBy(HeaderNormalizer.Normalize)
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.First());

        var order = 0;
        foreach (var def in definitions
                     .Where(d => d.IsActive && !d.Key.StartsWith(BillingFieldPrefix, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(d => d.Section).ThenBy(d => d.DisplayOrder).ThenBy(d => d.Label))
        {
            string? csvValue = null;
            foreach (var alias in def.NormalizedAliases())
            {
                if (alias.Length == 0 || !headerLookup.TryGetValue(alias, out var header) || consumedHeaders.Contains(header))
                    continue;
                consumedHeaders.Add(header);
                csvValue ??= record.GetCsvValue(header);
                if (csvValue is not null) break;
            }

            fields.Add(new WorkingField
            {
                Key = def.Key,
                Label = def.Label,
                Section = def.Section,
                FieldType = def.FieldType,
                IsRequired = def.IsRequired,
                RecipientEditable = def.RecipientEditable,
                HelpText = def.HelpText,
                MaxLength = def.MaxLength > 0 ? def.MaxLength : 250,
                Options = OptionParser.Parse(def.Options).ToList(),
                Order = order++,
                CsvValue = csvValue,
                Value = csvValue,
                Source = csvValue is null ? FieldSource.Empty : FieldSource.Csv,
            });
        }

        var usedKeys = new HashSet<string>(fields.Select(f => f.Key), StringComparer.Ordinal);
        foreach (var header in headers)
        {
            if (consumedHeaders.Contains(header)) continue;
            var normalized = HeaderNormalizer.Normalize(header);
            if (normalized.Length == 0) continue;

            var key = AdditionalFieldPrefix + normalized;
            for (var i = 2; usedKeys.Contains(key); i++) key = $"{AdditionalFieldPrefix}{normalized}{i}";
            usedKeys.Add(key);

            var csvValue = record.GetCsvValue(header);
            fields.Add(new WorkingField
            {
                Key = key,
                Label = header,
                Section = FormSection.Additional,
                FieldType = FieldType.Text,
                RecipientEditable = true,
                MaxLength = 500,
                Order = order++,
                CsvValue = csvValue,
                Value = csvValue,
                Source = csvValue is null ? FieldSource.Empty : FieldSource.Csv,
            });
        }

        foreach (var (key, label, type, required, maxLength, prefillFrom) in BillingDefinitions)
        {
            // Start from the matching store/owner value so the recipient usually only has to confirm it.
            var prefill = prefillFrom is null ? null : fields.FirstOrDefault(f => f.Key == prefillFrom)?.Value;
            fields.Add(new WorkingField
            {
                Key = key,
                Label = label,
                Section = FormSection.Billing,
                FieldType = type,
                IsRequired = required,
                RecipientEditable = true,
                MaxLength = maxLength > 0 ? maxLength : 250,
                Order = order++,
                CsvValue = prefill,
                Value = prefill,
                Source = prefill is null ? FieldSource.Empty : FieldSource.Csv,
            });
        }

        // The recipient chooses how the store pays; the card or bank details themselves are only ever entered on the
        // payment provider's page. Without bank payments enabled, card is the only method so no choice is shown.
        if (offerBankAccount)
        {
            fields.Add(new WorkingField
            {
                Key = BillingMethod,
                Label = "Payment method",
                Section = FormSection.Billing,
                FieldType = FieldType.Select,
                IsRequired = true,
                RecipientEditable = true,
                HelpText = "Card or bank details are entered later on the secure payment page, never on this form.",
                Options = PaymentMethods.Select(m => m.Name()).ToList(),
                Order = order++,
                Value = PaymentMethodKind.Card.Name(),   // default; no badge until someone changes it
                Source = FieldSource.Empty,
            });
        }

        return fields;
    }

    /// <summary>The payment method chosen on the form (card when the form offers no choice).</summary>
    public static PaymentMethodKind ChosenPaymentMethod(WorkingRecord record) =>
        ParsePaymentMethod(record.FindField(BillingMethod)?.Value) ?? PaymentMethodKind.Card;

    /// <summary>
    /// Reads the billing section into checkout details. Returns null with the problems found when a required
    /// billing value is missing or invalid.
    /// </summary>
    public static CheckoutDetails? ReadBilling(WorkingRecord record, PaymentMethodKind method, out List<string> problems)
    {
        problems = [];
        foreach (var def in BillingDefinitions)
        {
            var field = record.FindField(def.Key);
            var error = field is null
                ? def.Required ? $"{def.Label} is required." : null
                : FieldValidator.Validate(field, field.Value);
            if (error is not null) problems.Add(error);
        }
        if (problems.Count > 0) return null;

        string? Value(string key) => record.FindField(key)?.Value?.Trim();
        return new CheckoutDetails
        {
            BillingName = Value(BillingName)!,
            BillingEmail = Value(BillingEmail)!,
            AddressLine1 = Value(BillingAddress1)!,
            AddressLine2 = Value(BillingAddress2),
            City = Value(BillingCity)!,
            State = UsStates.Normalize(Value(BillingState))!,
            ZipCode = Value(BillingZip)!,
            PaymentMethod = method,
        };
    }

    /// <summary>Best-effort store name for display (form field first, then CSV).</summary>
    public static string? FindStoreName(WorkingRecord record, IReadOnlyList<string> headers)
    {
        var fromField = record.FindField("StoreName")?.Value;
        if (!string.IsNullOrWhiteSpace(fromField)) return fromField;
        foreach (var header in headers)
        {
            var n = HeaderNormalizer.Normalize(header);
            if (n is "storename" or "store" or "businessname" or "locationname")
                return record.GetCsvValue(header);
        }
        return null;
    }
}
