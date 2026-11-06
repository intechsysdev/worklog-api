using ClosedXML.Excel;
using WorkLog.Application.Abstractions;

namespace WorkLog.Infrastructure.Files;

/// <summary>Exporta a .xlsx con encabezado en negrilla, filtros y columnas ajustadas.</summary>
internal sealed class ClosedXmlSpreadsheetWriter : ISpreadsheetWriter
{
    public byte[] Write(string sheetName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        using var workbook = new XLWorkbook();
        var safeName = new string(sheetName.Where(c => !"[]*?/\\:".Contains(c)).Take(31).ToArray());
        var sheet = workbook.Worksheets.Add(string.IsNullOrWhiteSpace(safeName) ? "Datos" : safeName);

        for (var c = 0; c < headers.Count; c++)
            sheet.Cell(1, c + 1).Value = headers[c];

        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Count; c++)
            {
                var cell = sheet.Cell(r, c + 1);
                cell.Value = row[c] switch
                {
                    null => Blank.Value,
                    decimal d => d,
                    int i => i,
                    double dbl => dbl,
                    DateOnly date => date.ToDateTime(TimeOnly.MinValue),
                    var other => other.ToString()
                };
            }

            r++;
        }

        var header = sheet.Range(1, 1, 1, headers.Count);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#FBE9EE");
        sheet.Range(1, 1, Math.Max(1, r - 1), headers.Count).SetAutoFilter();
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
