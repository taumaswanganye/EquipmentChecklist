using EquipmentChecklist.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EquipmentChecklist.Services;

public class PdfService
{
    // ── Palette (matches the PSIC LDV reference form) ─────────────────────────
    private const string PageBg     = "#ffffff";
    private const string Ink        = "#0b1726";  // near-black text
    private const string InkMid     = "#374151";
    private const string Muted      = "#6b7280";
    private const string Line       = "#9ca3af";
    private const string LineSoft   = "#d1d5db";
    private const string FillSoft   = "#f3f4f6";
    private const string Brand      = "#1f3a93";  // brand accent (replaces logo colour)
    private const string NoGoRed    = "#c0392b";  // header bg for NO-GO rows
    private const string NoGoRedFg  = "#ffffff";
    private const string GoButYel   = "#f1c40f";  // header bg for GO-BUT rows
    private const string GoButYelFg = "#1a1a1a";
    private const string OkGreen    = "#166534";
    private const string DefectRed  = "#991b1b";

    // ── Branding / static config ───────────────────────────────────────────────
    // TODO: move these to appsettings.json if the operating site changes.
    private const string SiteName       = "KOLOMELA MINE";
    private const string DocumentNumber = "BCM-ENG-MIN-FRM-0001";

    private static readonly string[] EmergencyLines =
    {
        "All Emergencies",
        "Mobile  : —",
        "Landline: —",
        "Dispatch (two-way radio)"
    };

    private readonly IWebHostEnvironment _env;

    public PdfService(IWebHostEnvironment env)
    {
        _env = env;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //                       PRE-SHIFT INSPECTION CHECKLIST
    // ═══════════════════════════════════════════════════════════════════════════
    public byte[] GenerateChecklistPdf(ChecklistSubmission s)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var items = s.Items.OrderBy(i => i.TemplateItem.SortOrder).ToList();

        // Split into 2 columns, NO-GO items first then GO-BUT — matches reference.
        var ordered = items
            .OrderByDescending(i => i.TemplateItem.IsNoGoItem)
            .ThenBy(i => i.TemplateItem.SortOrder)
            .ToList();
        int half = (int)Math.Ceiling(ordered.Count / 2.0);
        var left  = ordered.Take(half).ToList();
        var right = ordered.Skip(half).ToList();
        int rows  = Math.Max(left.Count, right.Count);

        var shiftFull = s.Shift switch
        {
            Shift.Day       => "Day (A)",
            Shift.Afternoon => "Afternoon (B)",
            Shift.Night     => "Night (C)",
            _               => "—"
        };

        var machineLabel = s.Machine.TypeDisplay().ToUpper();

        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(22);
                page.PageColor(PageBg);
                page.DefaultTextStyle(ts => ts.FontSize(8.4f).FontColor(Ink).FontFamily("Helvetica"));

                page.Content().Column(col =>
                {
                    // ── BRAND BAR ─────────────────────────────────────────────
                    col.Item().PaddingBottom(8).Row(brand =>
                    {
                        brand.RelativeItem().Column(c =>
                        {
                            c.Item().Text(SiteName).Bold().FontSize(12).FontColor(Brand);
                            c.Item().Text("Pre-Shift Inspection Checklist").FontSize(8).FontColor(Muted);
                        });
                        brand.RelativeItem().AlignRight().AlignBottom()
                             .Text(SiteName.Replace(" MINE", " MINE")).FontSize(8).FontColor(InkMid);
                    });

                    // ── TITLE BAR ─────────────────────────────────────────────
                    col.Item().Border(0.8f).BorderColor(Ink).Background(FillSoft)
                       .PaddingVertical(7).PaddingHorizontal(10)
                       .AlignCenter()
                       .Text($"PRE-SHIFT INSPECTION CHECKLIST: {machineLabel}")
                       .Bold().FontSize(10.5f).FontColor(Ink);

                    // ── HEADER ROW: Date | Time | Shift | Odometer | Codes legend ─
                    col.Item().Border(0.8f).BorderColor(Ink).Row(hdr =>
                    {
                        FieldCell(hdr, "Date:",     s.SubmittedAt.ToString("dd MMM yyyy"));
                        FieldCell(hdr, "Time:",     s.SubmittedAt.ToString("HH:mm"));
                        FieldCell(hdr, "Shift:",    shiftFull);
                        FieldCell(hdr, "Odometer Reading (km):", s.KmOrHourMeter?.ToString() ?? "—");

                        // Codes legend on the right (NO-GO + GO-BUT)
                        hdr.RelativeItem(4).Column(legend =>
                        {
                            legend.Item().Padding(4).Text("The codes used to indicate deviations from standards:")
                                  .FontSize(6.6f).FontColor(InkMid).Italic();

                            legend.Item().BorderTop(0.5f).BorderColor(Line).Row(r =>
                            {
                                r.ConstantItem(60).AlignCenter().AlignMiddle().PaddingVertical(4)
                                 .Row(dot =>
                                 {
                                     dot.AutoItem().Background(NoGoRed)
                                        .Width(10).Height(10).PaddingTop(0);
                                     dot.AutoItem().PaddingLeft(4).AlignMiddle()
                                        .Text("NO-GO").Bold().FontSize(7).FontColor(Ink);
                                 });
                                r.RelativeItem().Background("#fde2e2").Padding(4)
                                 .Text("The TMM may not be operated, except to effect repairs. Defect(s) must be rectified before the TMM can be returned to production.")
                                 .FontSize(6.5f).FontColor(Ink);
                            });

                            legend.Item().BorderTop(0.5f).BorderColor(Line).Row(r =>
                            {
                                r.ConstantItem(60).AlignCenter().AlignMiddle().PaddingVertical(4)
                                 .Row(dot =>
                                 {
                                     dot.AutoItem().Background(GoButYel)
                                        .Width(10).Height(10).PaddingTop(0);
                                     dot.AutoItem().PaddingLeft(4).AlignMiddle()
                                        .Text("GO-BUT").Bold().FontSize(7).FontColor(Ink);
                                 });
                                r.RelativeItem().Background("#fef6c8").Padding(4)
                                 .Text("The TMM is safe to operate but must be attended to by an authorised artisan within the shift, under conditions defined by the maintenance supervisor.")
                                 .FontSize(6.5f).FontColor(Ink);
                            });
                        });
                    });

                    // ── SITE BAR ──────────────────────────────────────────────
                    col.Item().Border(0.8f).BorderColor(Ink).BorderTop(0)
                       .Background(FillSoft).AlignCenter().PaddingVertical(5)
                       .Text(SiteName).Bold().FontSize(11).FontColor(Ink);

                    // ── SLAM NR + Vehicle ID row ─────────────────────────────
                    col.Item().Border(0.8f).BorderColor(Ink).BorderTop(0).Row(r =>
                    {
                        FieldCell(r, "SLAM NR",                  "—");
                        FieldCell(r, "Vehicle ID / Equipment #", s.Machine.MachineNumber, 3);
                    });

                    // ── Sub-header bar: "Description / Activity" + P/O legend ─
                    col.Item().Border(0.8f).BorderColor(Ink).BorderTop(0).Row(r =>
                    {
                        r.RelativeItem(8).Background(FillSoft).PaddingVertical(5).AlignCenter()
                         .Text("Description / Activity").Bold().FontSize(10).FontColor(Ink);
                        r.RelativeItem(2).Background(FillSoft).Padding(4).Column(c =>
                        {
                            c.Item().Text(t =>
                            {
                                t.Span("P ").Bold().FontSize(10).FontColor(OkGreen);
                                t.Span("- In Order").FontSize(7).FontColor(Ink);
                            });
                            c.Item().Text(t =>
                            {
                                t.Span("O ").Bold().FontSize(10).FontColor(DefectRed);
                                t.Span("- Not in Order").FontSize(7).FontColor(Ink);
                            });
                        });
                    });

                    // ── TWO-COLUMN ITEM GRID ──────────────────────────────────
                    for (int i = 0; i < rows; i++)
                    {
                        var l = i < left.Count  ? left[i]  : null;
                        var rt = i < right.Count ? right[i] : null;
                        col.Item().Border(0.8f).BorderColor(Ink).BorderTop(0).Row(r =>
                        {
                            RenderItemHalf(r, l);
                            r.ConstantItem(0.8f).Background(Ink); // vertical divider
                            RenderItemHalf(r, rt);
                        });
                    }

                    // ── OPERATOR + REMARKS BLOCK ──────────────────────────────
                    col.Item().PaddingTop(8).Row(op =>
                    {
                        // LEFT: operator name, employee no, signature
                        op.RelativeItem(5).Border(0.8f).BorderColor(Ink).Column(c =>
                        {
                            LabeledBox(c, "Operator Name",   s.Operator?.FullName ?? "—");
                            LabeledBox(c, "Employee Number", s.Operator?.EmployeeNumber ?? "—");
                            c.Item().BorderTop(0.5f).BorderColor(Line).Padding(5).Column(sig =>
                            {
                                sig.Item().Text("Signature:").Bold().FontSize(8).FontColor(InkMid);
                                sig.Item().PaddingTop(2).Height(46).AlignCenter().AlignMiddle()
                                   .Element(e => EmbedSignature(e, s.OperatorSignature));
                            });
                        });

                        op.ConstantItem(6);

                        // RIGHT: remarks
                        op.RelativeItem(5).Border(0.8f).BorderColor(Ink).Padding(5).Column(c =>
                        {
                            c.Item().Text("Remarks:").Bold().FontSize(8).FontColor(InkMid);
                            c.Item().PaddingTop(3).MinHeight(85)
                             .Text(string.IsNullOrWhiteSpace(s.OperatorRemarks) ? " " : s.OperatorRemarks)
                             .FontSize(8).FontColor(Ink);
                        });
                    });

                    // ── EMERGENCY CONTACTS BAR ────────────────────────────────
                    col.Item().PaddingTop(8).Border(0.8f).BorderColor(Ink)
                       .Background(FillSoft).Padding(6).AlignCenter().Column(c =>
                    {
                        foreach (var line in EmergencyLines)
                            c.Item().AlignCenter().Text(line).FontSize(7.6f).FontColor(Ink);
                    });

                    // ── SUPERVISOR + ARTISAN SIGNATURES ───────────────────────
                    col.Item().PaddingTop(10).Row(sig =>
                    {
                        SigField(sig, "Supervisor Signature", s.Supervisor?.FullName,
                                 s.SupervisorSignature, s.SupervisorSignedAt);
                        sig.ConstantItem(20);
                        SigField(sig, "Artisan / Mechanic Signature",
                                 s.Mechanic?.FullName ?? s.RejectedMechanic?.FullName,
                                 null, s.MechanicSignedAt);
                    });

                    // ── FOOTER ────────────────────────────────────────────────
                    col.Item().PaddingTop(14).BorderTop(0.5f).BorderColor(Line)
                       .PaddingTop(4).Row(ft =>
                    {
                        ft.RelativeItem().Text($"DOCUMENT NUMBER: {DocumentNumber}")
                          .FontSize(7).FontColor(Muted).Bold();
                        ft.RelativeItem().AlignRight()
                          .Text($"Ref: BCM-{s.Id:D6}  ·  Generated {DateTime.UtcNow:dd MMM yyyy HH:mm} UTC")
                          .FontSize(7).FontColor(Muted);
                    });
                });
            });
        }).GeneratePdf();
    }

    // ───────────── helpers used by GenerateChecklistPdf ───────────────────────

    private static void FieldCell(RowDescriptor row, string label, string value, int flex = 2)
    {
        row.RelativeItem(flex).BorderRight(0.5f).BorderColor(Line).Padding(5).Column(c =>
        {
            c.Item().Text(label).FontSize(7).FontColor(InkMid);
            c.Item().PaddingTop(2).Text(value).Bold().FontSize(8.5f).FontColor(Ink);
        });
    }

    private static void LabeledBox(ColumnDescriptor col, string label, string value)
    {
        col.Item().BorderBottom(0.5f).BorderColor(Line).Padding(5).Row(r =>
        {
            r.ConstantItem(110).Text($"{label}:").Bold().FontSize(8).FontColor(InkMid);
            r.RelativeItem().Text(value).FontSize(8.5f).FontColor(Ink);
        });
    }

    private void RenderItemHalf(RowDescriptor row, SubmissionItem? item)
    {
        if (item == null)
        {
            row.RelativeItem(10).MinHeight(20).Text("");
            return;
        }

        var t        = item.TemplateItem;
        var isNoGo   = t.IsNoGoItem;
        var bg       = isNoGo ? NoGoRed  : GoButYel;
        var fg       = isNoGo ? NoGoRedFg : GoButYelFg;
        var ok       = item.Status == ItemStatus.InOrder;
        var statusCh = ok ? "P" : "O";
        var statusFg = ok ? OkGreen : DefectRed;

        // Icon cell — embed image if available, else short text marker
        row.ConstantItem(38).AlignCenter().AlignMiddle().Padding(2)
           .Height(22)
           .Element(e =>
           {
               var img = TryLoadIcon(t.IconPath);
               if (img != null) e.Image(img).FitArea();
               else             e.AlignCenter().AlignMiddle()
                                 .Text("•").Bold().FontSize(11).FontColor(Muted);
           });

        // Item label (coloured bar)
        row.RelativeItem(7).Background(bg).Padding(5).AlignMiddle().Column(c =>
        {
            var lbl = c.Item().Text(t.ItemName).Bold().FontSize(8).FontColor(fg);
            if (!ok && !string.IsNullOrWhiteSpace(item.Notes))
                c.Item().PaddingTop(2).Text($"↳ {item.Notes}")
                 .FontSize(6.6f).Italic().FontColor(fg);
        });

        // Status (P/O) cell
        row.ConstantItem(36).AlignCenter().AlignMiddle().Padding(4)
           .Text(statusCh).Bold().FontSize(14).FontColor(statusFg);
    }

    private static void SigField(RowDescriptor row, string label, string? name,
                                 string? signatureDataUrl, DateTime? when)
    {
        row.RelativeItem().Column(c =>
        {
            c.Item().Height(46).AlignCenter().AlignMiddle()
             .Element(e => EmbedSignature(e, signatureDataUrl));
            c.Item().BorderTop(0.8f).BorderColor(Ink).PaddingTop(2)
             .Text(label).Bold().FontSize(8).FontColor(Ink);
            c.Item().Text(name ?? "—").FontSize(8).FontColor(InkMid);
            if (when.HasValue)
                c.Item().Text(when.Value.ToString("dd MMM yyyy  HH:mm"))
                 .FontSize(7).FontColor(Muted);
        });
    }

    private static void EmbedSignature(IContainer container, string? dataUrl)
    {
        var bytes = DecodeDataUrl(dataUrl);
        if (bytes != null) container.Image(bytes).FitArea();
        else               container.Text(" ");
    }

    private static byte[]? DecodeDataUrl(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) return null;
        var i = dataUrl.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        try { return Convert.FromBase64String(dataUrl[(i + 7)..]); }
        catch { return null; }
    }

    /// <summary>Best-effort load of an item icon stored in wwwroot.</summary>
    private byte[]? TryLoadIcon(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath)) return null;
        try
        {
            var rel  = iconPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var full = Path.Combine(_env.WebRootPath ?? "wwwroot", rel);
            return File.Exists(full) ? File.ReadAllBytes(full) : null;
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //                          PARTS-ORDER PDF (unchanged)
    // ═══════════════════════════════════════════════════════════════════════════
    public byte[] GeneratePartsOrderPdf(
        string mechanicName,
        List<OrderLineItem> items,
        string orderRef)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(24);
                page.PageColor(PageBg);
                page.DefaultTextStyle(ts => ts.FontSize(9f).FontColor(Ink).FontFamily("Helvetica"));

                page.Content().Column(col =>
                {
                    col.Item().Background(Brand).Padding(14).Row(hdr =>
                    {
                        hdr.RelativeItem(3).Column(l =>
                        {
                            l.Item().Text(SiteName).Bold().FontSize(16).FontColor("#f59e0b");
                            l.Item().PaddingTop(2).Text("Equipment Maintenance Department")
                             .FontSize(8).FontColor("#d1d5db");
                        });
                        hdr.RelativeItem(3).AlignRight().Column(r =>
                        {
                            r.Item().AlignRight().Text("PARTS ORDER REQUEST")
                             .Bold().FontSize(13).FontColor("#ffffff");
                            r.Item().AlignRight().PaddingTop(3).Text($"Ref: {orderRef}")
                             .FontSize(8).FontColor("#9ca3af");
                        });
                    });

                    col.Item().PaddingTop(8);

                    col.Item().Background(FillSoft).Border(0.5f).BorderColor(LineSoft)
                       .Padding(10).Row(info =>
                    {
                        info.RelativeItem().Column(c =>
                        {
                            c.Item().Text("REQUESTED BY").FontSize(7).FontColor(Muted).Bold();
                            c.Item().PaddingTop(2).Text(mechanicName).FontSize(10).Bold().FontColor(Ink);
                        });
                        info.RelativeItem().Column(c =>
                        {
                            c.Item().Text("ORDER DATE").FontSize(7).FontColor(Muted).Bold();
                            c.Item().PaddingTop(2).Text(DateTime.UtcNow.ToString("dd MMMM yyyy")).FontSize(10).Bold().FontColor(Ink);
                        });
                        info.RelativeItem().Column(c =>
                        {
                            c.Item().Text("ITEMS").FontSize(7).FontColor(Muted).Bold();
                            c.Item().PaddingTop(2).Text(items.Count.ToString()).FontSize(10).Bold().FontColor(Ink);
                        });
                    });

                    col.Item().PaddingTop(12);

                    col.Item().Text("PARTS TO BE ORDERED").Bold().FontSize(10).FontColor(Brand);
                    col.Item().PaddingTop(4).Table(dt =>
                    {
                        dt.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(24);
                            c.RelativeColumn(2);
                            c.RelativeColumn(3);
                            c.RelativeColumn(3);
                            c.RelativeColumn(2);
                        });

                        dt.Header(th =>
                        {
                            PartsHdr(th.Cell(), "#");
                            PartsHdr(th.Cell(), "MACHINE");
                            PartsHdr(th.Cell(), "DEFECT / FAULT");
                            PartsHdr(th.Cell(), "PART REQUIRED");
                            PartsHdr(th.Cell(), "PART NUMBER");
                        });

                        for (int i = 0; i < items.Count; i++)
                        {
                            var item = items[i];
                            var bg = i % 2 == 0 ? PageBg : FillSoft;
                            dt.Cell().Background(bg).Padding(6).Text((i + 1).ToString()).FontColor(Muted);
                            dt.Cell().Background(bg).Padding(6).Column(c =>
                            {
                                c.Item().Text(item.MachineName).Bold().FontColor(Ink);
                                c.Item().Text(item.MachineNumber).FontSize(7.5f).FontColor(Muted);
                            });
                            dt.Cell().Background(bg).Padding(6).Text(item.DefectItem).FontColor(Ink);
                            dt.Cell().Background(bg).Padding(6).Text(item.PartRequired).Bold().FontColor(Brand);
                            dt.Cell().Background(bg).Padding(6).Text(item.PartNumber ?? "—").FontColor(Muted);
                        }
                    });

                    col.Item().PaddingTop(16);

                    col.Item().Background("#fef3c7").Border(0.8f).BorderColor("#d97706")
                       .Padding(10).Column(appr =>
                    {
                        appr.Item().Text("MANAGER APPROVAL").Bold().FontSize(9).FontColor("#92400e");
                        appr.Item().PaddingTop(6).Row(r =>
                        {
                            r.RelativeItem().Column(c =>
                            {
                                c.Item().Text("Approved by: ________________________").FontSize(9).FontColor(Ink);
                                c.Item().PaddingTop(14).Text("Signature: ________________________").FontSize(9).FontColor(Ink);
                            });
                            r.RelativeItem().Column(c =>
                            {
                                c.Item().Text("Date: __________________  /  __________________  /  ________").FontSize(9).FontColor(Ink);
                                c.Item().PaddingTop(14).Text("Notes: ________________________________________________").FontSize(9).FontColor(Ink);
                            });
                        });
                    });

                    col.Item().PaddingTop(10).Row(ft =>
                    {
                        ft.RelativeItem().Text($"{SiteName} · Maintenance Parts Order · System-generated document")
                          .FontSize(7).FontColor(Muted).Italic();
                        ft.RelativeItem().AlignRight()
                          .Text($"Ref: {orderRef}  ·  {DateTime.UtcNow:dd MMM yyyy HH:mm} UTC")
                          .FontSize(7).FontColor(Muted);
                    });
                });
            });
        }).GeneratePdf();
    }

    private static void PartsHdr(IContainer cell, string label) =>
        cell.Background(Brand).Padding(6).Text(label).Bold().FontSize(8).FontColor("#ffffff");
}
