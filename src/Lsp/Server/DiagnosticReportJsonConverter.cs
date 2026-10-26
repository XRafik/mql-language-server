using System;
using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// Wire-correct serializer for <see cref="RelatedDocumentDiagnosticReport"/> (issue #91).
///
/// <para>OmniSharp 0.19.9 attaches a <c>[JsonConverter]</c> to the abstract
/// <see cref="RelatedDocumentDiagnosticReport"/> whose <c>WriteJson</c> throws
/// <see cref="NotImplementedException"/> — pull diagnostics responses could
/// never be serialized. The exception struck inside the OutputHandler's
/// serialization loop, whose only recourse is to log at TRACE level
/// (invisible at default verbosity) and dispose the output pipeline
/// permanently: the <c>textDocument/diagnostic</c> response was never
/// delivered AND every subsequent response was silently dropped — the
/// "hang + permanent wedge" reported in issue #91.</para>
///
/// <para>Converters registered in <see cref="JsonSerializerSettings.Converters"/>
/// take precedence over the type-level attribute in Newtonsoft.Json, so this
/// converter — added to the <c>LspSerializer</c> in Program — restores
/// serialization for the two concrete report shapes this server emits,
/// per the LSP 3.17 wire format:</para>
///
/// <code>
/// { "kind": "full",      "resultId"?: string, "items": Diagnostic[] }
/// { "kind": "unchanged", "resultId": string }
/// </code>
///
/// <para>Deserialization is not implemented: the server never reads a
/// diagnostic report (it only produces them). <c>relatedDocuments</c> is not
/// emitted: the server never populates it, and the nested
/// <c>DocumentDiagnosticReport</c> converter is equally unimplemented in
/// 0.19.9 — emitting the property with a broken nested converter would
/// reintroduce the original failure.</para>
/// </summary>
public sealed class RelatedDocumentDiagnosticReportConverter : JsonConverter<RelatedDocumentDiagnosticReport>
{
    public override void WriteJson(JsonWriter writer, RelatedDocumentDiagnosticReport? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        switch (value)
        {
            case RelatedUnchangedDocumentDiagnosticReport unchanged:
                writer.WriteStartObject();
                WriteProperty(writer, "kind");
                writer.WriteValue("unchanged");
                WriteProperty(writer, "resultId");
                serializer.Serialize(writer, unchanged.ResultId);
                writer.WriteEndObject();
                break;

            default:
                // RelatedFullDocumentDiagnosticReport (the only full shape
                // this server produces; covers the abstract base default too).
                if (value is not RelatedFullDocumentDiagnosticReport full)
                {
                    // Unknown concrete report shape: do not re-enter the broken
                    // attribute converter — fail loudly BEFORE the response
                    // reaches the output loop (as a handler-side exception
                    // rather than an output-pipeline death).
                    throw new NotSupportedException(
                        $"Cannot serialize diagnostic report of type {value.GetType().FullName}: " +
                        "only RelatedFullDocumentDiagnosticReport and RelatedUnchangedDocumentDiagnosticReport are supported.");
                }

                writer.WriteStartObject();
                WriteProperty(writer, "kind");
                writer.WriteValue("full");
                if (full.ResultId != null)
                {
                    WriteProperty(writer, "resultId");
                    serializer.Serialize(writer, full.ResultId);
                }
                WriteProperty(writer, "items");
                serializer.Serialize(writer, full.Items);
                writer.WriteEndObject();
                break;
        }
    }

    public override RelatedDocumentDiagnosticReport? ReadJson(
        JsonReader reader, Type objectType, RelatedDocumentDiagnosticReport? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        // The server is the producer of diagnostic reports; it never consumes
        // one. Fail fast rather than silently misreading.
        throw new NotSupportedException(
            "Deserializing RelatedDocumentDiagnosticReport is not supported: the server only produces diagnostic reports.");
    }

    private static void WriteProperty(JsonWriter writer, string name) =>
        writer.WritePropertyName(name);
}