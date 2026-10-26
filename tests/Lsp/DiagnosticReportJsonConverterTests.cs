using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Serialization;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp;

/// <summary>
/// Issue #91: OmniSharp 0.19.9's [JsonConverter] on the abstract
/// RelatedDocumentDiagnosticReport throws NotImplementedException from
/// WriteJson — textDocument/diagnostic responses could never be serialized,
/// and the exception killed the OutputHandler pipeline permanently (the
/// silent hang + server wedge). These tests pin that the
/// RelatedDocumentDiagnosticReportConverter — registered at settings level,
/// which Newtonsoft takes precedence over the type attribute — produces the
/// wire-correct report JSON, and that the broken attribute path is exactly
/// what it replaces.
/// </summary>
public class DiagnosticReportJsonConverterTests
{
    private static LspSerializer CreateConfiguredSerializer()
    {
        // Mirror Program.cs registration (issue #91): the contract-resolver
        // subclass, NOT settings-level converters (which lose to the type
        // attribute's contract converter).
        return new MqlLspSerializer();
    }

    private static Diagnostic ExampleDiagnostic() => new Diagnostic
    {
        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
            new Position(1, 0), new Position(1, 12)),
        Severity = DiagnosticSeverity.Error,
        Message = "syntax error probe",
        Code = "1100",
        Source = "mql-lsp"
    };

    [Fact]
    public void FullReport_SerializesWireCorrectJson_WithoutThrowing()
    {
        var serializer = CreateConfiguredSerializer();

        var report = new RelatedFullDocumentDiagnosticReport
        {
            ResultId = "result-1",
            Items = new Container<Diagnostic>(new[] { ExampleDiagnostic() })
        };

        var json = serializer.SerializeObject(report);
        var obj = JObject.Parse(json);

        Assert.Equal("full", (string?)obj["kind"]);
        Assert.Equal("result-1", (string?)obj["resultId"]);
        var items = (JArray?)obj["items"];
        Assert.NotNull(items);
        var item = items!.OfType<JObject>().Single();
        Assert.Equal("syntax error probe", (string?)item["message"]);
        Assert.Equal("mql-lsp", (string?)item["source"]);
        Assert.Equal(1, (int)item["range"]!["start"]!["line"]!);
    }

    [Fact]
    public void FullReport_WithoutResultId_OmitsTheProperty()
    {
        var serializer = CreateConfiguredSerializer();

        var report = new RelatedFullDocumentDiagnosticReport
        {
            ResultId = null,
            Items = new Container<Diagnostic>(Array.Empty<Diagnostic>())
        };

        var json = serializer.SerializeObject(report);
        var obj = JObject.Parse(json);

        Assert.Equal("full", (string?)obj["kind"]);
        Assert.Null(obj["resultId"]);
        Assert.NotNull((JArray?)obj["items"]);
        Assert.Empty((JArray)obj["items"]!);
    }

    [Fact]
    public void UnchangedReport_SerializesWireCorrectJson()
    {
        var serializer = CreateConfiguredSerializer();

        var report = new RelatedUnchangedDocumentDiagnosticReport { ResultId = "result-2" };

        var json = serializer.SerializeObject(report);
        var obj = JObject.Parse(json);

        Assert.Equal("unchanged", (string?)obj["kind"]);
        Assert.Equal("result-2", (string?)obj["resultId"]);
    }

    [Fact]
    public void WithoutTheRegisteredConverter_TheStockAttributeConverter_ThrowsNotImplemented()
    {
        // Pin the root cause: the 0.19.9 attribute converter is exactly the
        // broken path this replacement fixes. A serializer upgrade that fixes
        // the attribute converter makes this test fail — delete the custom
        // converter with it.
        var stock = new LspSerializer();

        var report = new RelatedFullDocumentDiagnosticReport
        {
            ResultId = "result-1",
            Items = new Container<Diagnostic>(Array.Empty<Diagnostic>())
        };

        Assert.Throws<NotImplementedException>(() => stock.SerializeObject(report));
    }

    [Fact]
    public void RoundTripThroughTheOutgoingResponseShape_UsesSettingsConverters()
    {
        // The OutputHandler serializes responses with JsonConvert.SerializeObject(
        // value, Settings) — confirm the settings-level converter is picked up
        // through that exact entry point for a wrapper object too.
        var serializer = CreateConfiguredSerializer();

        var wrapper = new
        {
            jsonrpc = "2.0",
            id = 42,
            result = (object)new RelatedFullDocumentDiagnosticReport
            {
                ResultId = "result-3",
                Items = new Container<Diagnostic>(new[] { ExampleDiagnostic() })
            }
        };

        var json = serializer.SerializeObject(wrapper);
        var obj = JObject.Parse(json);

        Assert.Equal("full", (string?)obj["result"]!["kind"]);
        Assert.Equal(42, (int)obj["id"]!);
    }
}