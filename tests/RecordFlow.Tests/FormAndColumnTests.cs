using RecordFlow.Core;
using RecordFlow.Core.Entities;
using RecordFlow.Core.Services;
using RecordFlow.Core.Workspaces;

namespace RecordFlow.Tests;

public class FormBuilderTests
{
    private static readonly FormFieldDefinition[] Definitions =
    [
        new() { Key = "StoreName", Label = "Store Name", Section = FormSection.Store, CsvAliases = "Store Name, Business Name", IsRequired = true, IsActive = true },
        new() { Key = "StorePhone", Label = "Store Phone", Section = FormSection.Store, FieldType = FieldType.Phone, CsvAliases = "Store Phone Number, Phone", IsActive = true },
        new() { Key = "EmergencyContactPhone", Label = "Emergency Contact Phone", Section = FormSection.Owner, CsvAliases = "Owner Emergency Contact Number", IsActive = true },
        new() { Key = "Retired", Label = "Retired", Section = FormSection.Other, IsActive = false },
    ];

    [Fact]
    public void Maps_csv_columns_through_aliases_and_leaves_missing_fields_empty()
    {
        string[] headers = ["Contact ID", "business_name", "STORE PHONE NUMBER", "Region"];
        var record = new WorkingRecord
        {
            CsvValues = new() { ["Contact ID"] = "C-1", ["business_name"] = "Harbor Grocery", ["STORE PHONE NUMBER"] = "207-555-0100", ["Region"] = "NE" },
        };

        var fields = FormBuilder.Build(Definitions, record, headers, "Contact ID");

        var storeName = fields.Single(f => f.Key == "StoreName");
        Assert.Equal("Harbor Grocery", storeName.Value);
        Assert.Equal(FieldSource.Csv, storeName.Source);
        Assert.Equal("207-555-0100", fields.Single(f => f.Key == "StorePhone").Value);

        var emergency = fields.Single(f => f.Key == "EmergencyContactPhone");
        Assert.True(emergency.IsMissing);
        Assert.Equal(FieldSource.Empty, emergency.Source);

        Assert.DoesNotContain(fields, f => f.Key == "Retired");              // inactive definition
        Assert.DoesNotContain(fields, f => f.Label == "Contact ID");         // the key column is not a form field
        var extra = Assert.Single(fields, f => f.Section == FormSection.Additional);
        Assert.Equal("Region", extra.Label);                                 // unmatched CSV column is kept
        Assert.Equal("NE", extra.Value);
    }
}

public class AdminColumnResolverTests
{
    private static AdminColumn Column(int slot, string label, string? source = null, bool active = true, bool visible = true,
        bool required = false, string? defaultValue = null) =>
        new() { Slot = slot, Name = $"c{slot}", DisplayLabel = label, SourceField = source, IsActive = active, IsVisible = visible, IsRequired = required, DefaultValue = defaultValue, DisplayOrder = slot };

    private static WorkingRecord Record(params (string Header, string Value)[] cells) =>
        new() { CsvValues = cells.ToDictionary(c => c.Header, c => c.Value) };

    [Fact]
    public void Shows_only_columns_that_have_a_value_in_the_current_working_data()
    {
        string[] headers = ["Contact ID", "Store Name", "Region", "District"];
        var records = new[]
        {
            Record(("Store Name", "A"), ("Region", "West")),
            Record(("Store Name", "B")),
        };
        var columns = new[]
        {
            Column(1, "Store", "Store Name"),            // populated
            Column(2, "Region", "Region"),               // populated in one record
            Column(3, "District", "District"),           // header exists but every value is empty
            Column(4, "Territory", "Territory"),         // header missing entirely
            Column(5, "Inactive", "Store Name", active: false),
            Column(6, "Hidden", "Store Name", visible: false),
        };
        var resolver = new AdminColumnResolver(headers, []);

        var visible = resolver.VisibleColumns(columns, records);

        Assert.Equal(["Store", "Region"], visible.Select(c => c.DisplayLabel));
    }

    [Fact]
    public void Required_columns_stay_visible_so_users_can_fill_them()
    {
        var resolver = new AdminColumnResolver(["Contact ID"], []);
        var visible = resolver.VisibleColumns([Column(1, "Priority", required: true)], [Record()]);
        Assert.Single(visible);
    }

    [Fact]
    public void Resolution_order_is_user_value_then_form_then_csv_then_default()
    {
        var defs = new[] { new FormFieldDefinition { Key = "StoreName", Label = "Store Name", CsvAliases = "Business Name", IsActive = true } };
        var resolver = new AdminColumnResolver(["Business Name"], defs);
        var column = Column(1, "Store", "Store Name", defaultValue: "n/a");
        var record = Record(("Business Name", "From CSV"));

        Assert.Equal("From CSV", resolver.Resolve(column, record));                 // via form-field alias

        record.Fields = [new WorkingField { Key = "StoreName", Value = "Edited" }];
        Assert.Equal("Edited", resolver.Resolve(column, record));                   // live form value wins

        record.AdminColumnValues[1] = "Typed";
        Assert.Equal("Typed", resolver.Resolve(column, record));                    // user-entered value wins

        Assert.Equal("n/a", resolver.Resolve(column, Record()));                    // default when nothing else
    }

    [Fact]
    public void Closed_records_use_their_frozen_snapshot()
    {
        var resolver = new AdminColumnResolver(["Store Name"], []);
        var record = Record(("Store Name", "Live"));
        record.ClosedAdminColumnValues = new() { [1] = "Frozen" };
        Assert.Equal("Frozen", resolver.Resolve(Column(1, "Store", "Store Name"), record));
    }
}
