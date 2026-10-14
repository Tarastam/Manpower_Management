using System.Globalization;
using ClosedXML.Excel;
using ManpowerManagement.Models;
using ManpowerManagement.Pages.Attendance;

namespace ManpowerManagement.Services;

// Builds the .xlsx files the Report tab offers for download.
public static class ReportExcel
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    const string DateFormat = "yyyy-mm-dd";
    const string HoursFormat = "0.00";
    const string PercentFormat = "0.00%";

    public static byte[] Attendance(ReportService.AttendanceTable table)
    {
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Attendance");
        var fixedHeaders = new[] { "Emp ID", "Employee", "Workshop", "Process", "Shift" };
        var dayStart = fixedHeaders.Length + 1;
        var totalsStart = dayStart + table.Days.Count;
        var weeks = table.Weeks ?? [];
        Header(sheet, fixedHeaders.Concat(table.Days.Select(d => d.ToString("dd/MM"))).Concat(["Worked hours", "Leave hours"]).Concat(weeks.Select((w, i) => $"Week {i + 1} ({w.Label}) hours")));
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i]; var r = i + 2;
            sheet.Cell(r, 1).Value = row.Code; sheet.Cell(r, 2).Value = row.Name; sheet.Cell(r, 3).Value = row.Workshop;
            sheet.Cell(r, 4).Value = row.Process; sheet.Cell(r, 5).Value = row.Shift;
            for (var d = 0; d < row.Cells.Count; d++)
            {
                var cell = sheet.Cell(r, dayStart + d); var text = row.Cells[d];
                if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var hours)) { cell.Value = hours; cell.Style.NumberFormat.Format = HoursFormat; }
                else
                {
                    cell.Value = text;
                    if (text == "OFF") cell.Style.Fill.BackgroundColor = XLColor.LightGray;
                    else if (text == ReportService.Resigned) cell.Style.Fill.BackgroundColor = XLColor.LightPink;
                    else if (text != "") cell.Style.Fill.BackgroundColor = XLColor.LightGoldenrodYellow;
                }
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                // The day's remark becomes an Excel note on that cell.
                if (row.DayNotes is { } dayNotes && d < dayNotes.Count && dayNotes[d] != "")
                {
                    var note = cell.CreateComment();
                    note.AddText(dayNotes[d]);
                    note.Style.Size.SetAutomaticSize();
                }
            }
            sheet.Cell(r, totalsStart).Value = row.WorkedHours; sheet.Cell(r, totalsStart).Style.NumberFormat.Format = HoursFormat;
            sheet.Cell(r, totalsStart + 1).Value = row.LeaveHours; sheet.Cell(r, totalsStart + 1).Style.NumberFormat.Format = HoursFormat;
            for (var w = 0; w < weeks.Count && row.WeekHours is { } weekHours && w < weekHours.Count; w++)
            {
                var weekCell = sheet.Cell(r, totalsStart + 2 + w);
                weekCell.Value = weekHours[w]; weekCell.Style.NumberFormat.Format = HoursFormat;
            }
        }
        Finish(sheet, 2);
        sheet.Range(1, dayStart, 1, totalsStart - 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        // Day columns hold short codes, so keep them narrow instead of letting a long remark stretch the sheet.
        for (var c = dayStart; c < totalsStart; c++) sheet.Column(c).Width = 7;
        return Save(book);
    }

    // Raw table: one row per day, workshop and shift with the manpower and how many people took each leave type.
    public static byte[] LeaveTrend(ReportService.LeaveTrend trend)
    {
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Leave by day");
        string[] lead = ["Date", "Workshop", "Shift", "Manpower"], totals = ["Total leave (incl. AL)", "Total leave (excl. AL)"], tail = ["%Total Leave (incl. AL)", "%Total Leave (excl. AL)"];
        Header(sheet, lead.Concat(ReportService.LeaveTypes.Select(t => IndexModel.LeaveCodes[t])).Concat(totals)
            .Concat(ReportService.LeaveTypes.Select(t => "%" + IndexModel.LeaveCodes[t])).Concat(tail));
        var leaveEnd = 4 + ReportService.LeaveTypes.Length;
        for (var i = 0; i < trend.Rows.Count; i++)
        {
            var row = trend.Rows[i]; var r = i + 2;
            sheet.Cell(r, 1).Value = row.Date.ToDateTime(TimeOnly.MinValue); sheet.Cell(r, 1).Style.NumberFormat.Format = DateFormat;
            sheet.Cell(r, 2).Value = row.Workshop; sheet.Cell(r, 3).Value = row.Shift; sheet.Cell(r, 4).Value = row.Manpower;
            for (var t = 0; t < ReportService.LeaveTypes.Length; t++) sheet.Cell(r, 5 + t).Value = row.Of(ReportService.LeaveTypes[t]);
            sheet.Cell(r, leaveEnd + 1).Value = row.Total;
            sheet.Cell(r, leaveEnd + 2).Value = row.TotalWithoutAl;
            var percentStart = leaveEnd + 3;
            for (var t = 0; t < ReportService.LeaveTypes.Length; t++) Percent(sheet.Cell(r, percentStart + t), row.Of(ReportService.LeaveTypes[t]), row.Manpower);
            Percent(sheet.Cell(r, percentStart + ReportService.LeaveTypes.Length), row.Total, row.Manpower);
            Percent(sheet.Cell(r, percentStart + ReportService.LeaveTypes.Length + 1), row.TotalWithoutAl, row.Manpower);
        }
        Finish(sheet, 3);
        return Save(book);
    }

    public static byte[] WorkingHours(IReadOnlyList<ReportService.HoursRow> rows)
    {
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Working hours");
        Header(sheet, ["Date", "Workshop", "Shift", "Total hours", "Indirect hours", "Direct hours", "Total headcount", "Indirect headcount", "Direct headcount"]);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var r = i + 2;
            sheet.Cell(r, 1).Value = row.Date.ToDateTime(TimeOnly.MinValue); sheet.Cell(r, 1).Style.NumberFormat.Format = DateFormat;
            sheet.Cell(r, 2).Value = row.Workshop; sheet.Cell(r, 3).Value = row.Shift;
            sheet.Cell(r, 4).Value = row.Total; sheet.Cell(r, 5).Value = row.Indirect; sheet.Cell(r, 6).Value = row.Direct;
            sheet.Range(r, 4, r, 6).Style.NumberFormat.Format = HoursFormat;
            sheet.Cell(r, 7).Value = row.TotalCount; sheet.Cell(r, 8).Value = row.IndirectCount; sheet.Cell(r, 9).Value = row.DirectCount;
        }
        Finish(sheet, 0);
        return Save(book);
    }

    public static byte[] Employees(IReadOnlyList<ReportService.EmployeeRow> rows)
    {
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Employees");
        Header(sheet, ["Emp ID", "Name", "Job Grade", "Process", "Workshop", "Shift", "Status", "Start Date", "Resigned Date", "New Employee", "Special Groups"]);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var r = i + 2;
            sheet.Cell(r, 1).Value = row.Code; sheet.Cell(r, 2).Value = row.Name; sheet.Cell(r, 3).Value = row.JobGrade;
            sheet.Cell(r, 4).Value = row.Process; sheet.Cell(r, 5).Value = row.Workshop; sheet.Cell(r, 6).Value = row.Shift; sheet.Cell(r, 7).Value = row.Status;
            sheet.Cell(r, 8).Value = row.StartDate.ToDateTime(TimeOnly.MinValue); sheet.Cell(r, 8).Style.NumberFormat.Format = DateFormat;
            if (row.ResignedDate is DateOnly resigned) { sheet.Cell(r, 9).Value = resigned.ToDateTime(TimeOnly.MinValue); sheet.Cell(r, 9).Style.NumberFormat.Format = DateFormat; }
            sheet.Cell(r, 10).Value = row.IsNewEmployee ? "Yes" : "No"; sheet.Cell(r, 11).Value = row.Groups;
        }
        Finish(sheet, 0);
        return Save(book);
    }

    // The Dashboard's Headcount Summary and Work Station Performance tables on one sheet, in the same colours.
    public static byte[] Summary(WorkforceDashboardService.Report report)
    {
        const string Ink = "#17213B", Blue = "#1527C9", Muted = "#68728B", CountFormat = "#,##0", BalanceFormat = "#,##0;(#,##0);0";
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Summary");
        sheet.ShowGridLines = false;
        sheet.Style.Font.FontName = "Calibri";
        sheet.Style.Font.FontColor = XLColor.FromHtml(Ink);
        sheet.Column(1).Width = 34;
        sheet.Columns(2, 4).Width = 17;
        var mp = report.Manpower;
        var monthLabel = DateTime.ParseExact(report.Month, "yyyy-MM", CultureInfo.InvariantCulture).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        var asOf = report.AsOfNote;

        sheet.Cell(1, 1).Value = "Summary · " + monthLabel;
        sheet.Cell(1, 1).Style.Font.FontSize = 16;
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(2, 1).Value = asOf;
        sheet.Cell(2, 1).Style.Font.FontColor = XLColor.FromHtml(Muted);

        // Section heading, then its header row: each cell is (text, background, font colour). Returns the first body row.
        int Start(int row, string title, (string Text, string Back, string Fore)[] header)
        {
            var heading = sheet.Cell(row, 1);
            heading.Value = title;
            heading.Style.Font.Bold = true;
            heading.Style.Font.FontSize = 14;
            heading.Style.Font.FontColor = XLColor.FromHtml("#1F6FE5");
            for (var c = 0; c < header.Length; c++)
            {
                var cell = sheet.Cell(row + 1, c + 1);
                cell.Value = header[c].Text;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml(header[c].Back);
                cell.Style.Font.FontColor = XLColor.FromHtml(header[c].Fore);
                cell.Style.Font.Bold = true;
                cell.Style.Alignment.Horizontal = c == 0 ? XLAlignmentHorizontalValues.Left : XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            }
            sheet.Row(row + 1).Height = 28;
            return row + 2;
        }

        // One body row: the label then three colours/values per column; null back/fore leaves that cell plain.
        void Body(int row, string label, int?[] numbers, string?[] back, string?[] fore, bool boldNumbers, bool boldLabel, double height = 22)
        {
            var label0 = sheet.Cell(row, 1);
            label0.Value = label;
            label0.Style.Font.Bold = boldLabel;
            for (var c = 0; c < 4; c++)
            {
                var cell = sheet.Cell(row, c + 1);
                if (c > 0 && numbers[c - 1] is int n) { cell.Value = n; cell.Style.NumberFormat.Format = CountFormat; cell.Style.Font.Bold = boldNumbers; }
                cell.Style.Alignment.Horizontal = c == 0 ? XLAlignmentHorizontalValues.Left : XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                if (back[c] is { } b) cell.Style.Fill.BackgroundColor = XLColor.FromHtml(b);
                if (fore[c] is { } f) cell.Style.Font.FontColor = XLColor.FromHtml(f);
            }
            sheet.Row(row).Height = height;
        }

        void Frame(int first, int last)
        {
            var range = sheet.Range(first, 1, last, 4);
            range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.InsideBorderColor = XLColor.FromHtml("#EDF0F7");
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.OutsideBorderColor = XLColor.FromHtml("#E1E7F3");
        }

        void Note(int row, string text)
        {
            var range = sheet.Range(row, 1, row, 4).Merge();
            range.Value = text;
            range.Style.Alignment.WrapText = true;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            range.Style.Font.FontSize = 9;
            range.Style.Font.Italic = true;
            range.Style.Font.FontColor = XLColor.FromHtml(Muted);
            sheet.Row(row).Height = 38;
        }

        // Headcount Summary
        var r = Start(4, "Headcount Summary", [("Category", "#E8EFFC", Ink), ("Chemical Line", "#E8EFFC", Ink), ("Assembly Line", "#E8EFFC", Ink), ("Total", "#3B82F6", "#FFFFFF")]);
        var headcountFirst = r - 1;
        foreach (var row in mp.Support)
        {
            var colour = SafeHex(row.ColorHex);
            Body(r++, row.Label, [row.First, row.Second, row.Total], [null, null, null, null], [colour, colour, colour, Blue], boldNumbers: false, boldLabel: true);
            sheet.Cell(r - 1, 4).Style.Font.Bold = true;
        }
        if (mp.Support.Count == 0)
        {
            var empty = sheet.Range(r, 1, r, 4).Merge();
            empty.Value = "No active special groups. Add them on the Special Groups page.";
            empty.Style.Font.FontColor = XLColor.FromHtml(Muted);
            empty.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            r++;
        }
        const string Grey = "#E9EBF2";
        Body(r++, "Total support", [mp.FirstSupport, mp.SecondSupport, mp.TotalSupport], [Grey, Grey, Grey, Grey], [Ink, Ink, Ink, Blue], boldNumbers: false, boldLabel: true);
        sheet.Cell(r - 1, 4).Style.Font.Bold = true;
        Body(r++, "Normal Operator", [mp.FirstHeadcount - mp.FirstSupport, mp.SecondHeadcount - mp.SecondSupport, mp.TotalHeadcount - mp.TotalSupport],
            ["#1F6FE5", "#1F6FE5", "#1F6FE5", "#2F80F5"], ["#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF"], boldNumbers: true, boldLabel: true, height: 26);
        Body(r++, "Total Operator", [mp.FirstHeadcount, mp.SecondHeadcount, mp.TotalHeadcount],
            ["#FFF7D6", "#FFF7D6", "#FFF7D6", "#FFF7D6"], [Ink, Ink, Ink, Blue], boldNumbers: true, boldLabel: true, height: 26);
        Frame(headcountFirst, r - 1);
        Note(r++, "Headcount on the last day of the period for 51T-52T employees; the workshop filter does not apply. Each row is an active special group, and Total support counts a tagged person once.");

        // Work Station Performance
        r = Start(r + 1, "Work Station Performance", [("W/S", "#E8EFFC", Ink), ("New target", "#FDE8E0", Ink), ("Actual", "#E8EFFC", Ink), ("Balance", "#D9F3E4", Ink)]);
        var stationFirst = r - 1;
        foreach (var row in mp.Rows)
        {
            // Colours as on the Dashboard: whole-row fill and text, with the Target and Actual cells and the Balance pill on top.
            var (fill, text, targetFill, actualText, bold) = row.Kind switch
            {
                WorkforceDashboardService.ManpowerRowKind.Subtotal or WorkforceDashboardService.ManpowerRowKind.GrandSubtotal => ("#FFF27A", Ink, "#FFF27A", Ink, true),
                WorkforceDashboardService.ManpowerRowKind.Staff => (Grey, Ink, Grey, Blue, false),
                WorkforceDashboardService.ManpowerRowKind.DirectLabor => ("#7CC4FB", Ink, "#7CC4FB", Ink, true),
                WorkforceDashboardService.ManpowerRowKind.GrandTotal => ("#0F5FD6", "#FFFFFF", "#0F5FD6", "#FFFFFF", true),
                _ => ((string?)null, Ink, "#FDF0EA", Blue, false)
            };
            var grand = row.Kind == WorkforceDashboardService.ManpowerRowKind.GrandTotal;
            Body(r, row.Label, [row.Target, row.Actual, null], [fill, targetFill, fill, fill], [text, text, actualText, text],
                boldNumbers: bold, boldLabel: row.Kind != WorkforceDashboardService.ManpowerRowKind.Staff, height: grand ? 26 : 22);
            sheet.Cell(r, 3).Style.Font.Bold = true;
            var balance = sheet.Cell(r, 4);
            string pillFill, pillText;
            if (row.Balance is int b)
            {
                balance.Value = b;
                balance.Style.NumberFormat.Format = BalanceFormat;
                (pillFill, pillText) = b switch { < 0 => ("#FDE0E7", "#C0143C"), 0 => ("#E6E9F0", "#5B6479"), _ => ("#D3F2DF", "#17663A") };
            }
            else
            {
                balance.Value = "-";
                (pillFill, pillText) = ("#E6E9F0", "#5B6479");
            }
            balance.Style.Fill.BackgroundColor = XLColor.FromHtml(pillFill);
            balance.Style.Font.FontColor = XLColor.FromHtml(pillText);
            balance.Style.Font.Bold = true;
            r++;
        }
        Frame(stationFirst, r - 1);
        Note(r, "Actual is the headcount on the last day of the period by job grade (51T-52T per workshop, 53T-55T per line) across all workshops. Balance = Actual − New target.");

        sheet.PageSetup.PageOrientation = XLPageOrientation.Portrait;
        sheet.PageSetup.FitToPages(1, 1);
        return Save(book);
    }

    // Special group colours are admin-edited text: accept a plain #RGB or #RRGGBB value, otherwise fall back to grey.
    static string SafeHex(string? hex)
    {
        if (hex is { Length: 4 or 7 } && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit))
            return hex.Length == 7 ? hex : "#" + string.Concat(hex.Skip(1).Select(c => $"{c}{c}"));
        return "#6C757D";
    }

    static void Percent(IXLCell cell, int people, int manpower)
    {
        cell.Value = (double)people / manpower;
        cell.Style.NumberFormat.Format = PercentFormat;
    }

    static void Header(IXLWorksheet sheet, IEnumerable<string> headers)
    {
        var column = 1;
        foreach (var header in headers) sheet.Cell(1, column++).Value = header;
        var range = sheet.Range(1, 1, 1, column - 1);
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#E9E5F7");
    }

    // Frozen header row (and the first columns when asked), then widths fitted to the content.
    static void Finish(IXLWorksheet sheet, int frozenColumns)
    {
        sheet.SheetView.Freeze(1, frozenColumns);
        sheet.Columns().AdjustToContents();
    }

    static byte[] Save(XLWorkbook book)
    {
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }
}
