using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AVAQueryBuilder.Agents.Mcp;

internal static class Json
{
    /// <summary>
    /// How answers are written: plainly, so an agent reads "°F", "SpO₂" and "…" rather than their
    /// \u escapes. Nothing here is put into a web page.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
