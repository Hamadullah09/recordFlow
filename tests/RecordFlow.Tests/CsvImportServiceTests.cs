using System.Text;
using Microsoft.Extensions.Options;
using RecordFlow.Core.Abstractions;
using RecordFlow.Infrastructure.Csv;

namespace RecordFlow.Tests;

public class CsvImportServiceTests
{
    private static CsvImportService Create(Action<CsvImportOptions>? configure = null)
    {
        var options = new CsvImportOptions();
        configure?.Invoke(options);
        return new CsvImportService(Options.Create(options), TimeProvider.System);
    }

    private static Task<CsvImportResult> Import(string csv, string fileName = "stores.csv", CsvImportService? service = null) =>
        (service ?? Create()).ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), fileName);

    [Fact]
    public async Task Imports_rows_and_tolerates_missing_fields()
    {
        var result = await Import("Contact ID,Store Name,Store Phone\nC-1,Main St Market,(555) 222-1111\nC-2,Harbor,\n");

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal("C-1", result.Records[0].ContactId);
        Assert.Equal("Main St Market", result.Records[0].GetCsvValue("Store Name"));
        Assert.Null(result.Records[1].GetCsvValue("Store Phone")); // missing value stays empty
        Assert.Equal(0, result.ContactIdHeaderIndex);
    }

    [Fact]
    public async Task Assigns_temporary_ids_when_contact_id_column_is_missing()
    {
        var result = await Import("Store Name\nA\nB\n");

        Assert.True(result.Succeeded);
        Assert.Equal(["ROW-0001", "ROW-0002"], result.Records.Select(r => r.ContactId));
        Assert.All(result.Records, r => Assert.True(r.ContactIdGenerated));
        Assert.Contains(result.Warnings, w => w.Contains("Contact ID"));
    }

    [Fact]
    public async Task Each_record_gets_a_unique_unguessable_key()
    {
        var result = await Import("Contact ID\n1\n2\n3\n");
        Assert.Equal(3, result.Records.Select(r => r.Key).Distinct().Count());
        Assert.All(result.Records, r => Assert.True(r.Key.Length >= 16));
    }

    [Fact]
    public async Task Rejects_files_without_csv_extension()
    {
        var result = await Import("Contact ID\n1\n", "stores.xlsx");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Rejects_binary_content_disguised_as_csv()
    {
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 }; // ZIP / XLSX signature
        var result = await Create().ImportAsync(new MemoryStream(bytes), "stores.csv");
        Assert.False(result.Succeeded);
        Assert.Contains("does not look like a CSV", result.Errors[0]);
    }

    [Fact]
    public async Task Rejects_files_over_the_size_limit()
    {
        var service = Create(o => o.MaxFileBytes = 50);
        var result = await Import("Contact ID,Store Name\n" + string.Join("\n", Enumerable.Range(1, 20).Select(i => $"{i},Store {i}")), service: service);
        Assert.False(result.Succeeded);
        Assert.Contains("limit", result.Errors[0]);
    }

    [Fact]
    public async Task Rejects_files_with_too_many_rows()
    {
        var service = Create(o => o.MaxRows = 2);
        var result = await Import("Contact ID\n1\n2\n3\n", service: service);
        Assert.False(result.Succeeded);
        Assert.Empty(result.Records);
    }

    [Fact]
    public async Task Rejects_header_only_and_empty_files()
    {
        Assert.False((await Import("Contact ID,Store Name\n")).Succeeded);
        Assert.False((await Import("")).Succeeded);
    }

    [Fact]
    public async Task Handles_bom_semicolon_delimiter_blank_rows_and_duplicate_headers()
    {
        var csv = "﻿Contact ID;Phone;Phone\nC-9;111;222\n;;\n";
        var result = await Import(csv);

        Assert.True(result.Succeeded);
        Assert.Single(result.Records);
        Assert.Equal(["Contact ID", "Phone", "Phone (2)"], result.Headers);
        Assert.Equal("222", result.Records[0].GetCsvValue("Phone (2)"));
    }

    [Fact]
    public async Task Strips_control_characters_from_values()
    {
        var result = await Import("Contact ID,Store Name\nC-1,\"Main\u0007 Market\u001B\"\n");
        Assert.Equal("Main Market", result.Records[0].GetCsvValue("Store Name"));
    }

    [Fact]
    public async Task Treats_nul_bytes_as_binary_content()
    {
        var result = await Import("Contact ID,Store Name\nC-1,Main\u0000Market\n");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Supports_quoted_values_with_commas_and_newlines()
    {
        var result = await Import("Contact ID,Address Line 1,Notes\nC-1,\"12 Main St, Suite 4\",\"line one\nline two\"\n");
        Assert.Equal("12 Main St, Suite 4", result.Records[0].GetCsvValue("Address Line 1"));
        Assert.Equal("line one\nline two", result.Records[0].GetCsvValue("Notes"));
    }
}
