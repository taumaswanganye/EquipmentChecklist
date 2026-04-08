using EquipmentChecklist.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EquipmentChecklist.Services;

public class PdfService
{
    // ── Light theme colour palette (matching paper Excel form) ─────────────────
    private static readonly string PageBg      = "#ffffff";
    private static readonly string HeaderBg    = "#1e3a5f";   // dark navy
    private static readonly string HeaderFg    = "#ffffff";
    private static readonly string SubHeaderBg = "#2d6a9f";
    private static readonly string SubHeaderFg = "#ffffff";
    private static readonly string ColHdrBg    = "#2d6a9f";
    private static readonly string ColHdrFg    = "#ffffff";
    private static readonly string AltRow      = "#f0f4f8";
    private static readonly string BorderCol   = "#b0c4d8";
    private static readonly string TextDark    = "#1a1a2e";
    private static readonly string TextMid     = "#374151";
    private static readonly string TextMuted   = "#6b7280";
    private static readonly string OkGreen     = "#166534";
    private static readonly string DefectRed   = "#991b1b";
    private static readonly string NoGoBg      = "#fee2e2";
    private static readonly string GoButBg     = "#fef3c7";
    private static readonly string GoServiceBg = "#dbeafe";
    private static readonly string GoBg        = "#dcfce7";
    private static readonly string SigBoxBg    = "#f8fafc";

    public byte[] GenerateChecklistPdf(ChecklistSubmission s)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var items   = s.Items.OrderBy(i => i.TemplateItem.SortOrder).ToList();
        var defects = items.Where(i => i.Status == ItemStatus.Defect).ToList();

        var (statusBg, statusLabel, statusFg) = s.Status switch
        {
            ChecklistStatus.Go                => (GoBg,        "GO",                     "#166534"),
            ChecklistStatus.GoButRepair24H    => (GoButBg,     "GO BUT – REPAIR 24H",    "#92400e"),
            ChecklistStatus.GoTillNextService => (GoServiceBg, "GO TILL NEXT SERVICE",   "#1e40af"),
            ChecklistStatus.NoGo              => (NoGoBg,      "NO-GO – IMMOBILISED",    "#991b1b"),
            ChecklistStatus.Rejected          => (NoGoBg,      "REJECTED BY SUPERVISOR", "#991b1b"),
            _                                 => ("#f3f4f6",   "PENDING REVIEW",         "#374151")
        };

        var shiftLabel = s.Shift switch
        {
            Shift.Day       => "A",
            Shift.Afternoon => "B",
            Shift.Night     => "C",
            _               => "?"
        };
        var shiftFull = s.Shift switch
        {
            Shift.Day       => "DAY (A)",
            Shift.Afternoon => "AFTERNOON (B)",
            Shift.Night     => "NIGHT (C)",
            _               => s.Shift.ToString()
        };

        // Split items into two columns
        var half  = (int)Math.Ceiling(items.Count / 2.0);
        var left  = items.Take(half).ToList();
        var right = items.Skip(half).ToList();
        var rows  = Math.Max(left.Count, right.Count);

        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(10);
                page.PageColor(PageBg);
                page.DefaultTextStyle(ts => ts.FontSize(8f).FontColor(TextDark).FontFamily("Arial"));

                page.Content().Column(col =>
                {
                    // ── MAIN HEADER ────────────────────────────────────────────────
                    col.Item().Background(HeaderBg).Row(hdr =>
                    {
                        // LEFT: Mine name + form title
                        hdr.RelativeItem(4).Padding(8).Column(lft =>
                        {
                            lft.Item().Text("BELFAST COAL MINE")
                               .Bold().FontSize(14).FontColor("#f59e0b");
                            lft.Item().PaddingTop(2).Text("PRE-USE INSPECTION CHECKLIST")
                               .Bold().FontSize(9).FontColor("#e5e7eb");
                        });

                        // CENTER: Machine info
                        hdr.RelativeItem(5).AlignCenter().Padding(8).Column(mid =>
                        {
                            mid.Item().AlignCenter().Text(s.Machine.MachineName.ToUpper())
                               .Bold().FontSize(12).FontColor(HeaderFg);
                            mid.Item().AlignCenter().PaddingTop(2)
                               .Text($"Machine No: {s.Machine.MachineNumber}")
                               .FontSize(9).FontColor("#d1d5db");
                        });

                        // RIGHT: Status badge
                        hdr.RelativeItem(3).Padding(6).AlignRight().Column(rgt =>
                        {
                            rgt.Item().AlignRight().Background(statusBg)
                               .Border(1f).BorderColor(statusFg)
                               .Padding(6).Text(statusLabel)
                               .Bold().FontSize(9).FontColor(statusFg);
                        });
                    });

                    // ── OPERATOR INFO ROW ───────────────────────────────────────────
                    col.Item().Background(SubHeaderBg).Padding(0).Row(info =>
                    {
                        void InfoBox(RowDescriptor r, string lbl, string val, int flex = 1)
                        {
                            r.RelativeItem(flex).BorderRight(0.5f).BorderColor("#4b9fd8")
                             .Padding(5).Column(c =>
                            {
                                c.Item().Text(lbl).FontSize(6.5f).FontColor("#bfdbfe").Bold();
                                c.Item().PaddingTop(1).Text(val).FontSize(8f).Bold().FontColor(SubHeaderFg);
                            });
                        }
                        InfoBox(info, "OPERATOR", $"{s.Operator.FullName}  ({s.Operator.EmployeeNumber})", 3);
                        InfoBox(info, "DATE", s.SubmittedAt.ToString("dd MMM yyyy"), 2);
                        InfoBox(info, "TIME", s.SubmittedAt.ToString("HH:mm"), 1);
                        InfoBox(info, "SHIFT", shiftFull, 1);
                        InfoBox(info, "KM / HOURS", s.KmOrHourMeter?.ToString() ?? "–", 2);
                    });

                    col.Item().PaddingTop(3);

                    // ── SHIFT LEGEND + STATUS LEGEND ROW ───────────────────────────
                    col.Item().Row(legend =>
                    {
                        // Shift boxes A / B / C
                        legend.RelativeItem(3).Border(0.8f).BorderColor(BorderCol).Padding(5).Row(sh =>
                        {
                            sh.AutoItem().PaddingRight(6)
                              .Text("SHIFT:").Bold().FontSize(7).FontColor(TextMid);
                            foreach (var (ltr, name) in new[] { ("A","Day"), ("B","Afternoon"), ("C","Night") })
                            {
                                var selected = shiftLabel == ltr;
                                sh.AutoItem().PaddingRight(8).Row(sr =>
                                {
                                    sr.AutoItem()
                                      .Width(12).Height(12)
                                      .Background(selected ? "#1e3a5f" : "#f8fafc")
                                      .Border(0.8f).BorderColor(BorderCol)
                                      .AlignCenter().AlignMiddle()
                                      .Text(selected ? "✓" : "")
                                      .Bold().FontSize(8).FontColor(selected ? "#ffffff" : TextMid);
                                    sr.AutoItem().PaddingLeft(3)
                                      .Text($"{ltr} – {name}").FontSize(7).FontColor(TextMid);
                                });
                            }
                        });

                        legend.ConstantItem(6);

                        // Status legend boxes
                        legend.RelativeItem(7).Border(0.8f).BorderColor(BorderCol).Padding(5).Row(sl =>
                        {
                            sl.AutoItem().PaddingRight(6)
                              .Text("STATUS:").Bold().FontSize(7).FontColor(TextMid);
                            foreach (var (bg, fg, lbl) in new[]
                            {
                                (NoGoBg,      "#991b1b", "NO GO"),
                                (GoButBg,     "#92400e", "GO BUT – REPAIR WITHIN 24H"),
                                (GoServiceBg, "#1e40af", "GO TILL NEXT SERVICE"),
                                (GoBg,        "#166534", "GO")
                            })
                            {
                                var isActive = statusLabel.Contains(lbl.Split(" – ")[0]);
                                sl.AutoItem().PaddingRight(8).Row(sr =>
                                {
                                    sr.AutoItem()
                                      .Width(12).Height(12)
                                      .Background(isActive ? bg : "#f8fafc")
                                      .Border(0.8f).BorderColor(isActive ? fg : BorderCol)
                                      .AlignCenter().AlignMiddle()
                                      .Text(isActive ? "✓" : "")
                                      .Bold().FontSize(8).FontColor(isActive ? fg : TextMid);
                                    var lblText = sr.AutoItem().PaddingLeft(3)
                                      .Text(lbl).FontSize(7).FontColor(isActive ? fg : TextMid);
                                    if (isActive) lblText.Bold();
                                });
                            }
                        });
                    });

                    col.Item().PaddingTop(3);

                    // ── CHECKLIST COLUMN HEADERS ────────────────────────────────────
                    col.Item().Background(ColHdrBg).Row(ch =>
                    {
                        // Left column header
                        ch.ConstantItem(20).AlignCenter().PaddingVertical(5)
                          .Text("No.").Bold().FontSize(7).FontColor(ColHdrFg);
                        ch.RelativeItem().PaddingLeft(4).PaddingVertical(5)
                          .Text("CHECKLIST ITEM").Bold().FontSize(7).FontColor(ColHdrFg);
                        ch.ConstantItem(38).AlignCenter().PaddingVertical(5)
                          .Text("IN ORDER").Bold().FontSize(6.5f).FontColor(ColHdrFg);
                        ch.ConstantItem(38).AlignCenter().PaddingVertical(5)
                          .Text("DEFECT").Bold().FontSize(6.5f).FontColor(ColHdrFg);

                        // Divider
                        ch.ConstantItem(3).Background("#1a3255");

                        // Right column header
                        ch.ConstantItem(20).AlignCenter().PaddingVertical(5)
                          .Text("No.").Bold().FontSize(7).FontColor(ColHdrFg);
                        ch.RelativeItem().PaddingLeft(4).PaddingVertical(5)
                          .Text("CHECKLIST ITEM").Bold().FontSize(7).FontColor(ColHdrFg);
                        ch.ConstantItem(38).AlignCenter().PaddingVertical(5)
                          .Text("IN ORDER").Bold().FontSize(6.5f).FontColor(ColHdrFg);
                        ch.ConstantItem(38).AlignCenter().PaddingVertical(5)
                          .Text("DEFECT").Bold().FontSize(6.5f).FontColor(ColHdrFg);
                    });

                    // ── CHECKLIST ITEM ROWS (2-column grid) ─────────────────────────
                    for (int r = 0; r < rows; r++)
                    {
                        var bg     = r % 2 == 0 ? PageBg : AltRow;
                        var leftItem  = r < left.Count  ? left[r]  : null;
                        var rightItem = r < right.Count ? right[r] : null;

                        col.Item().Background(bg)
                           .BorderBottom(0.4f).BorderColor(BorderCol)
                           .Row(row =>
                        {
                            RenderPaperItemRow(row, leftItem, r + 1, true);

                            // Divider
                            row.ConstantItem(3).Background(BorderCol);

                            RenderPaperItemRow(row, rightItem, half + r + 1, false);
                        });
                    }

                    col.Item().PaddingTop(4);

                    // ── DEFECTS TABLE (if any) ──────────────────────────────────────
                    if (defects.Any())
                    {
                        col.Item().Background(NoGoBg)
                           .Border(1f).BorderColor("#fca5a5")
                           .Padding(6).Column(def =>
                        {
                            def.Item().Text($"DEFECTS FOUND  ({defects.Count})")
                               .Bold().FontSize(8.5f).FontColor(DefectRed);
                            def.Item().PaddingTop(4).Table(dt =>
                            {
                                dt.ColumnsDefinition(c =>
                                {
                                    c.RelativeColumn(4);
                                    c.RelativeColumn(4);
                                    c.RelativeColumn(2);
                                });

                                dt.Header(th =>
                                {
                                    DefectHeaderCell(th.Cell(), "CHECKLIST ITEM");
                                    DefectHeaderCell(th.Cell(), "OPERATOR NOTES");
                                    DefectHeaderCell(th.Cell(), "SEVERITY");
                                });

                                foreach (var d in defects)
                                {
                                    dt.Cell().Padding(4).Text(d.TemplateItem.ItemName).FontColor(DefectRed);
                                    dt.Cell().Padding(4).Text(d.Notes ?? "–").FontColor(TextMid);
                                    dt.Cell().Padding(4).Text(
                                        d.TemplateItem.IsNoGoItem ? "CRITICAL / NO-GO" : "Defect")
                                      .Bold().FontColor(d.TemplateItem.IsNoGoItem ? DefectRed : "#92400e");
                                }
                            });
                        });

                        col.Item().PaddingTop(4);
                    }

                    // ── FITNESS DECLARATION ─────────────────────────────────────────
                    col.Item().Background(SigBoxBg)
                       .Border(0.8f).BorderColor(BorderCol)
                       .Padding(6).Row(fd =>
                    {
                        fd.RelativeItem(8).Column(c =>
                        {
                            c.Item().Text("FITNESS DECLARATION").Bold().FontSize(7.5f).FontColor(HeaderBg);
                            c.Item().PaddingTop(2).Text(
                                "\"I am healthy, in good physical condition and mental state, well rested and do not " +
                                "feel fatigued in order to operate this equipment.\"")
                               .FontSize(7.5f).FontColor(TextMuted).Italic();
                        });
                        fd.RelativeItem(2).AlignCenter().AlignMiddle().Column(c =>
                        {
                            c.Item().AlignCenter()
                             .Background(s.FitnessDeclarationSigned ? GoBg : NoGoBg)
                             .Border(0.8f).BorderColor(s.FitnessDeclarationSigned ? "#166534" : DefectRed)
                             .Padding(4)
                             .Text(s.FitnessDeclarationSigned ? "✓  SIGNED" : "✕  NOT SIGNED")
                             .Bold().FontSize(8).FontColor(s.FitnessDeclarationSigned ? OkGreen : DefectRed);
                        });
                    });

                    // ── OPERATOR REMARKS (if any) ──────────────────────────────────
                    if (!string.IsNullOrEmpty(s.OperatorRemarks))
                    {
                        col.Item().PaddingTop(3).Background(SigBoxBg)
                           .Border(0.8f).BorderColor(BorderCol).Padding(6).Row(rem =>
                        {
                            rem.AutoItem().PaddingRight(8)
                               .Text("REMARKS:").Bold().FontSize(7.5f).FontColor(HeaderBg);
                            rem.RelativeItem()
                               .Text(s.OperatorRemarks).FontSize(8).FontColor(TextDark);
                        });
                    }

                    col.Item().PaddingTop(3);

                    // ── SIGNATURE BLOCKS ────────────────────────────────────────────
                    col.Item().Background(SigBoxBg)
                       .Border(0.8f).BorderColor(BorderCol)
                       .Row(sig =>
                    {
                        PaperSigBlock(sig, "OPERATOR SIGNATURE",
                            s.Operator.FullName,
                            s.Operator.EmployeeNumber,
                            s.SubmittedAt.ToString("dd MMM yyyy  HH:mm"));

                        sig.ConstantItem(1).Background(BorderCol);

                        PaperSigBlock(sig, "SUPERVISOR SIGNATURE",
                            s.Supervisor?.FullName ?? "Pending",
                            "",
                            s.SupervisorSignedAt?.ToString("dd MMM yyyy  HH:mm") ?? "–");

                        sig.ConstantItem(1).Background(BorderCol);

                        PaperSigBlock(sig, "MECHANIC / TECHNICIAN",
                            s.Mechanic?.FullName ?? s.RejectedMechanic?.FullName ?? "–",
                            "",
                            s.MechanicSignedAt?.ToString("dd MMM yyyy  HH:mm") ?? "–");
                    });

                    // ── FOOTER ──────────────────────────────────────────────────────
                    col.Item().PaddingTop(4).Row(ft =>
                    {
                        ft.RelativeItem()
                          .Text("Belfast Coal Mine · MHSA / DMR / CPS Level 8/9 Compliant · " +
                                "This is a system-generated document. Do not alter.")
                          .FontSize(6.5f).FontColor(TextMuted).Italic();
                        ft.RelativeItem().AlignRight()
                          .Text($"Ref: BCM-{s.Id:D6}  |  Generated: {DateTime.UtcNow:dd MMM yyyy HH:mm} UTC")
                          .FontSize(6.5f).FontColor(TextMuted);
                    });
                });
            });
        }).GeneratePdf();
    }

    // ── Generate a parts-order PDF to email to manager ─────────────────────────
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
                page.DefaultTextStyle(ts => ts.FontSize(9f).FontColor(TextDark).FontFamily("Arial"));

                page.Content().Column(col =>
                {
                    // Header
                    col.Item().Background(HeaderBg).Padding(14).Row(hdr =>
                    {
                        hdr.RelativeItem(3).Column(l =>
                        {
                            l.Item().Text("BELFAST COAL MINE")
                             .Bold().FontSize(16).FontColor("#f59e0b");
                            l.Item().PaddingTop(2).Text("Equipment Maintenance Department")
                             .FontSize(8).FontColor("#d1d5db");
                        });
                        hdr.RelativeItem(3).AlignRight().Column(r =>
                        {
                            r.Item().AlignRight().Text("PARTS ORDER REQUEST")
                             .Bold().FontSize(13).FontColor(HeaderFg);
                            r.Item().AlignRight().PaddingTop(3).Text($"Ref: {orderRef}")
                             .FontSize(8).FontColor("#9ca3af");
                        });
                    });

                    col.Item().PaddingTop(8);

                    // Order details row
                    col.Item().Background(AltRow).Border(0.5f).BorderColor(BorderCol)
                       .Padding(10).Row(info =>
                    {
                        info.RelativeItem().Column(c =>
                        {
                            c.Item().Text("REQUESTED BY").FontSize(7).FontColor(TextMuted).Bold();
                            c.Item().PaddingTop(2).Text(mechanicName).FontSize(10).Bold().FontColor(TextDark);
                        });
                        info.RelativeItem().Column(c =>
                        {
                            c.Item().Text("ORDER DATE").FontSize(7).FontColor(TextMuted).Bold();
                            c.Item().PaddingTop(2).Text(DateTime.UtcNow.ToString("dd MMMM yyyy")).FontSize(10).Bold().FontColor(TextDark);
                        });
                        info.RelativeItem().Column(c =>
                        {
                            c.Item().Text("ITEMS").FontSize(7).FontColor(TextMuted).Bold();
                            c.Item().PaddingTop(2).Text(items.Count.ToString()).FontSize(10).Bold().FontColor(TextDark);
                        });
                    });

                    col.Item().PaddingTop(12);

                    // Parts table
                    col.Item().Text("PARTS TO BE ORDERED").Bold().FontSize(10).FontColor(HeaderBg);
                    col.Item().PaddingTop(4).Table(dt =>
                    {
                        dt.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(24);  // #
                            c.RelativeColumn(2);   // Machine
                            c.RelativeColumn(3);   // Defect Item
                            c.RelativeColumn(3);   // Part Required
                            c.RelativeColumn(2);   // Part Number
                        });

                        dt.Header(th =>
                        {
                            PartsTableHeaderCell(th.Cell(), "#");
                            PartsTableHeaderCell(th.Cell(), "MACHINE");
                            PartsTableHeaderCell(th.Cell(), "DEFECT / FAULT");
                            PartsTableHeaderCell(th.Cell(), "PART REQUIRED");
                            PartsTableHeaderCell(th.Cell(), "PART NUMBER");
                        });

                        for (int i = 0; i < items.Count; i++)
                        {
                            var item = items[i];
                            var bg = i % 2 == 0 ? PageBg : AltRow;
                            dt.Cell().Background(bg).Padding(6).Text((i + 1).ToString())
                              .FontColor(TextMuted);
                            dt.Cell().Background(bg).Padding(6).Column(c =>
                            {
                                c.Item().Text(item.MachineName).Bold().FontColor(TextDark);
                                c.Item().Text(item.MachineNumber).FontSize(7.5f).FontColor(TextMuted);
                            });
                            dt.Cell().Background(bg).Padding(6).Text(item.DefectItem).FontColor(TextDark);
                            dt.Cell().Background(bg).Padding(6).Text(item.PartRequired)
                              .Bold().FontColor(HeaderBg);
                            dt.Cell().Background(bg).Padding(6)
                              .Text(item.PartNumber ?? "–").FontColor(TextMuted);
                        }
                    });

                    col.Item().PaddingTop(16);

                    // Approval box
                    col.Item().Background(GoButBg).Border(0.8f).BorderColor("#d97706")
                       .Padding(10).Column(appr =>
                    {
                        appr.Item().Text("MANAGER APPROVAL").Bold().FontSize(9).FontColor("#92400e");
                        appr.Item().PaddingTop(6).Row(r =>
                        {
                            r.RelativeItem().Column(c =>
                            {
                                c.Item().Text("Approved by: ________________________")
                                 .FontSize(9).FontColor(TextDark);
                                c.Item().PaddingTop(14).Text("Signature: ________________________")
                                 .FontSize(9).FontColor(TextDark);
                            });
                            r.RelativeItem().Column(c =>
                            {
                                c.Item().Text("Date: __________________  /  __________________  /  ________")
                                 .FontSize(9).FontColor(TextDark);
                                c.Item().PaddingTop(14).Text("Notes: ________________________________________________")
                                 .FontSize(9).FontColor(TextDark);
                            });
                        });
                    });

                    // Footer
                    col.Item().PaddingTop(10).Row(ft =>
                    {
                        ft.RelativeItem()
                          .Text("Belfast Coal Mine · Maintenance Parts Order · System-generated document")
                          .FontSize(7).FontColor(TextMuted).Italic();
                        ft.RelativeItem().AlignRight()
                          .Text($"Ref: {orderRef}  |  {DateTime.UtcNow:dd MMM yyyy HH:mm} UTC")
                          .FontSize(7).FontColor(TextMuted);
                    });
                });
            });
        }).GeneratePdf();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static void RenderPaperItemRow(RowDescriptor row, SubmissionItem? item, int num, bool isLeft)
    {
        if (item == null)
        {
            row.ConstantItem(20).Text("").FontSize(7);
            row.RelativeItem().Text("").FontSize(7);
            row.ConstantItem(38).Text("").FontSize(7);
            row.ConstantItem(38).Text("").FontSize(7);
            return;
        }

        var isOk   = item.Status == ItemStatus.InOrder;
        var isCrit = item.TemplateItem.IsNoGoItem;

        row.ConstantItem(20).AlignCenter().PaddingVertical(3)
           .Text(num.ToString()).FontSize(7).FontColor(TextMuted);

        row.RelativeItem().PaddingLeft(3).PaddingVertical(3).Column(c =>
        {
            var itemText = c.Item().Text($"{ItemIcon(item.TemplateItem.ItemName)}  {item.TemplateItem.ItemName}")
             .FontSize(7.5f)
             .FontColor(isCrit && !isOk ? DefectRed : TextDark);
            if (isCrit) itemText.Bold();
            if (!isOk && !string.IsNullOrEmpty(item.Notes))
                c.Item().PaddingLeft(10).Text($"↳ {item.Notes}")
                 .FontSize(6.5f).FontColor(TextMuted).Italic();
        });

        // In Order checkbox
        row.ConstantItem(38).AlignCenter().PaddingVertical(3)
           .Text(isOk ? "✓" : "").FontSize(10).Bold().FontColor(OkGreen);

        // Defect checkbox
        row.ConstantItem(38).AlignCenter().PaddingVertical(3)
           .Text(!isOk ? "✕" : "").FontSize(10).Bold()
           .FontColor(isCrit ? DefectRed : "#d97706");
    }

    private static void DefectHeaderCell(IContainer cell, string label)
    {
        cell.Background("#b91c1c").Padding(5).Text(label)
            .Bold().FontSize(7.5f).FontColor("#fef2f2");
    }

    private static void PartsTableHeaderCell(IContainer cell, string label)
    {
        cell.Background(HeaderBg).Padding(6).Text(label)
            .Bold().FontSize(8).FontColor(HeaderFg);
    }

    private static void PaperSigBlock(RowDescriptor row, string role, string name, string empNo, string date)
    {
        row.RelativeItem().Padding(8).Column(c =>
        {
            c.Item().Text(role).Bold().FontSize(7).FontColor(HeaderBg);
            c.Item().PaddingTop(2).Text(name).FontSize(8.5f).FontColor(TextDark);
            if (!string.IsNullOrEmpty(empNo))
                c.Item().Text($"Emp#: {empNo}").FontSize(7).FontColor(TextMuted);
            c.Item().PaddingTop(2).Text(date).FontSize(7).FontColor(TextMuted);
            c.Item().PaddingTop(12).BorderBottom(0.8f).BorderColor(BorderCol).Text(" ");
            c.Item().PaddingTop(2).Text("Signature").FontSize(6.5f).FontColor(TextMuted).Italic();
        });
    }

    // ── Icon map: printable short text codes ───────────────────────────────────
    private static string ItemIcon(string name)
    {
        name = name.ToUpper();
        if (name.Contains("LICENCE") || name.Contains("LICENSE"))       return "[ID]";
        if (name.Contains("FIRE EXT"))                                   return "[FX]";
        if (name.Contains("SEAT BELT") || name.Contains("SEATBELT"))    return "[SB]";
        if (name.Contains("BRAKE"))                                      return "[BR]";
        if (name.Contains("LIGHT") || name.Contains("LAMP"))            return "[LT]";
        if (name.Contains("MIRROR"))                                     return "[MR]";
        if (name.Contains("TYRE") || name.Contains("TIRE"))             return "[TY]";
        if (name.Contains("WHEEL NUT"))                                  return "[WN]";
        if (name.Contains("OIL"))                                        return "[OL]";
        if (name.Contains("FUEL"))                                       return "[FL]";
        if (name.Contains("COOLANT") || name.Contains("RADIATOR"))      return "[CL]";
        if (name.Contains("HOOTER") || name.Contains("HORN"))           return "[HN]";
        if (name.Contains("RADIO") || name.Contains("TWO WAY") || name.Contains("TWO-WAY")) return "[RD]";
        if (name.Contains("WIPER") || name.Contains("WINDSCREEN"))      return "[WP]";
        if (name.Contains("AIR COND"))                                   return "[AC]";
        if (name.Contains("DOOR") || name.Contains("HANDLE"))           return "[DR]";
        if (name.Contains("WINDOW"))                                     return "[WW]";
        if (name.Contains("STEP"))                                       return "[ST]";
        if (name.Contains("SEAT"))                                       return "[SE]";
        if (name.Contains("HYDRAULIC"))                                  return "[HY]";
        if (name.Contains("TRACK") || name.Contains("CHAIN"))           return "[TR]";
        if (name.Contains("BUCKET") || name.Contains("BLADE") || name.Contains("FORK")) return "[AT]";
        if (name.Contains("ISOLATION") || name.Contains("EMERGENCY STOP")) return "[ES]";
        if (name.Contains("KEY"))                                        return "[KY]";
        if (name.Contains("REFLECTIVE") || name.Contains("TAPE"))       return "[RF]";
        if (name.Contains("GAUGE") || name.Contains("DASHBOARD") || name.Contains("INSTRUMENT")) return "[GG]";
        if (name.Contains("STOP BLOCK"))                                 return "[BK]";
        if (name.Contains("FLAG"))                                       return "[FG]";
        if (name.Contains("BOOM") || name.Contains("CRANE"))            return "[CR]";
        if (name.Contains("PIN"))                                        return "[PN]";
        if (name.Contains("HAND RAIL") || name.Contains("GRIP"))        return "[HR]";
        if (name.Contains("GUARD"))                                      return "[GD]";
        if (name.Contains("HOSE"))                                       return "[HS]";
        if (name.Contains("PUMP"))                                       return "[PM]";
        if (name.Contains("NOZZLE"))                                     return "[NZ]";
        if (name.Contains("OUTRIGGER"))                                  return "[OR]";
        if (name.Contains("BUMP"))                                       return "[BM]";
        if (name.Contains("ALARM") || name.Contains("BUZZER"))          return "[AL]";
        if (name.Contains("STEERING"))                                   return "[SW]";
        if (name.Contains("LEVER") || name.Contains("JOYSTICK") || name.Contains("CONTROL")) return "[CT]";
        return "[--]";
    }
}
