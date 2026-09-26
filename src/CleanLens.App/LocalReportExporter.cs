using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace CleanLens.App;

public static class LocalReportExporter
{
    public static void Export(string path, string title, IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<string>> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        if (Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase)) WriteHtml(writer, title, columns, rows);
        else WriteCsv(writer, columns, rows);
    }

    private static void WriteCsv(TextWriter writer, IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<string>> rows)
    {
        static string Cell(string value)
        {
            var first = value.TrimStart();
            if (first.StartsWith('=') || first.StartsWith('+') || first.StartsWith('-') || first.StartsWith('@')) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        writer.WriteLine(string.Join(',', columns.Select(Cell)));
        foreach (var row in rows) writer.WriteLine(string.Join(',', row.Select(Cell)));
    }

    private static void WriteHtml(TextWriter writer, string title, IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<string>> rows)
    {
        static string Safe(string value) => WebUtility.HtmlEncode(value);
        writer.Write("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>");
        writer.Write(Safe(title));
        writer.Write("</title><style>body{font:14px 'Segoe UI',sans-serif;background:#f4f7fb;color:#13243a;margin:0;padding:32px}main{max-width:1400px;margin:auto}h1{font-size:28px;margin:0 0 8px}.meta{color:#64748b;margin-bottom:24px}.table{background:white;border:1px solid #dce5ef;border-radius:16px;overflow:auto}table{border-collapse:collapse;width:100%}th{text-align:left;background:#eaf1fa;color:#315072;font-size:11px;letter-spacing:.08em;text-transform:uppercase}th,td{padding:13px 16px;border-bottom:1px solid #e6edf5;vertical-align:top;overflow-wrap:anywhere}tr:nth-child(even){background:#f9fbfd}</style><main><h1>");
        writer.Write(Safe(title));
        writer.Write("</h1><p class=\"meta\">CleanLens · ");
        writer.Write(Safe(DateTimeOffset.Now.ToString("g", CultureInfo.CurrentCulture)));
        writer.Write("</p><div class=\"table\"><table><thead><tr>");
        foreach (var column in columns) { writer.Write("<th>"); writer.Write(Safe(column)); writer.Write("</th>"); }
        writer.Write("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            writer.Write("<tr>");
            foreach (var cell in row) { writer.Write("<td>"); writer.Write(Safe(cell)); writer.Write("</td>"); }
            writer.Write("</tr>");
        }
        writer.Write("</tbody></table></div></main></html>");
    }
}
