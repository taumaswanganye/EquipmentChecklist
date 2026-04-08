using System.Net;
using System.Net.Mail;
using System.Net.Mime;

namespace EquipmentChecklist.Services;

public class EmailService
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<EmailService> _log;

    public EmailService(IConfiguration cfg, ILogger<EmailService> log)
    {
        _cfg = cfg;
        _log = log;
    }

    // ── Send order confirmation to mechanic + parts order PDF to manager ───────
    /// <summary>Returns true if email was sent successfully, false if skipped or failed.</summary>
    public async Task<bool> SendOrderConfirmationAsync(
        string toEmail,
        string toName,
        List<OrderLineItem> items,
        string orderRef,
        byte[]? partsOrderPdf = null)
    {
        var smtp      = _cfg["Email:SmtpHost"]    ?? "smtp.gmail.com";
        var port      = int.Parse(_cfg["Email:SmtpPort"] ?? "587");
        var user      = _cfg["Email:Username"]    ?? "";
        var pass      = _cfg["Email:Password"]    ?? "";
        var from      = _cfg["Email:From"]        ?? user;
        var fromName  = _cfg["Email:FromName"]    ?? "Belfast Equipment System";
        var managerEmail = _cfg["Email:ManagerEmail"];

        if (string.IsNullOrEmpty(user))
        {
            _log.LogWarning("Email not configured – skipping send. Set Email:* in appsettings.json");
            return false;
        }

        var rows = string.Join("", items.Select(i => $"""
            <tr style="border-bottom:1px solid #e5e7eb">
              <td style="padding:10px 12px">{i.MachineName} ({i.MachineNumber})</td>
              <td style="padding:10px 12px">{i.DefectItem}</td>
              <td style="padding:10px 12px;font-weight:600">{i.PartRequired}</td>
              <td style="padding:10px 12px;color:#6b7280">{i.PartNumber ?? "–"}</td>
            </tr>
        """));

        var mechanicBody = BuildMechanicEmailHtml(toName, orderRef, rows, items.Count);
        var managerBody  = BuildManagerEmailHtml(toName, orderRef, rows, items.Count);

        try
        {
            using var client       = new SmtpClient(smtp, port);
            client.EnableSsl       = true;
            client.Credentials     = new NetworkCredential(user, pass);

            // ── Email 1: confirmation to mechanic ─────────────────────────────
            var mechMsg = new MailMessage
            {
                From       = new MailAddress(from, fromName),
                Subject    = $"[Belfast] Parts Order #{orderRef} – {items.Count} item(s) submitted",
                Body       = mechanicBody,
                IsBodyHtml = true,
            };
            mechMsg.To.Add(new MailAddress(toEmail, toName));

            await client.SendMailAsync(mechMsg);
            _log.LogInformation("Order confirmation sent to mechanic {Email}", toEmail);

            // ── Email 2: parts order PDF to manager ───────────────────────────
            if (!string.IsNullOrEmpty(managerEmail))
            {
                var mgrMsg = new MailMessage
                {
                    From       = new MailAddress(from, fromName),
                    Subject    = $"[Belfast] PARTS ORDER REQUIRED – {items.Count} item(s)  |  Ref #{orderRef}",
                    Body       = managerBody,
                    IsBodyHtml = true,
                };
                mgrMsg.To.Add(managerEmail);

                // Attach PDF if provided
                if (partsOrderPdf != null && partsOrderPdf.Length > 0)
                {
                    var stream     = new MemoryStream(partsOrderPdf);
                    var attachment = new Attachment(stream, $"PartsOrder_{orderRef}.pdf",
                                                    MediaTypeNames.Application.Pdf);
                    mgrMsg.Attachments.Add(attachment);
                }

                await client.SendMailAsync(mgrMsg);
                _log.LogInformation("Parts order email sent to manager {Email}", managerEmail);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to send order emails for ref {Ref}", orderRef);
            // Don't throw – email failure should not break the order submission
            return false;
        }
    }

    // ── Email bodies ──────────────────────────────────────────────────────────

    private static string BuildMechanicEmailHtml(
        string toName, string orderRef, string rows, int count)
    {
        return $"""
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8"/></head>
        <body style="font-family:Arial,sans-serif;background:#f4f4f4;margin:0;padding:0">
          <div style="max-width:620px;margin:32px auto;background:#fff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,0.1)">
            <div style="background:#1e3a5f;padding:24px 28px">
              <div style="color:#f59e0b;font-weight:800;font-size:18px;text-transform:uppercase">Belfast Coal Mine</div>
              <div style="color:#9ca3af;font-size:12px">Digital Equipment Checklist System</div>
            </div>
            <div style="padding:28px">
              <h2 style="margin:0 0 8px;color:#1e3a5f">Parts Order Confirmation</h2>
              <p style="color:#6b7280;margin:0 0 20px">Order reference: <strong style="color:#1e3a5f">#{orderRef}</strong></p>
              <p style="margin:0 0 20px">Hi <strong>{toName}</strong>, your parts order has been submitted and sent to the manager for approval.</p>
              <table style="width:100%;border-collapse:collapse;font-size:13px">
                <thead>
                  <tr style="background:#f0f4f8">
                    <th style="padding:10px 12px;text-align:left;border-bottom:2px solid #b0c4d8">Machine</th>
                    <th style="padding:10px 12px;text-align:left;border-bottom:2px solid #b0c4d8">Defect Item</th>
                    <th style="padding:10px 12px;text-align:left;border-bottom:2px solid #b0c4d8">Part Required</th>
                    <th style="padding:10px 12px;text-align:left;border-bottom:2px solid #b0c4d8">Part #</th>
                  </tr>
                </thead>
                <tbody>{rows}</tbody>
              </table>
              <div style="margin-top:24px;padding:16px;background:#f0fdf4;border:1px solid #bbf7d0;border-radius:6px">
                <strong style="color:#15803d">✓ Order submitted</strong>
                <p style="margin:4px 0 0;color:#166534;font-size:13px">
                  This order has been sent to the manager. Parts will be sourced and delivered to the workshop once approved.
                </p>
              </div>
            </div>
            <div style="padding:16px 28px;background:#f9fafb;border-top:1px solid #e5e7eb;font-size:11px;color:#9ca3af">
              Belfast Coal Mine · MHSA / DMR / CPS Level 8/9 Compliant · Auto-generated – do not reply
            </div>
          </div>
        </body>
        </html>
        """;
    }

    private static string BuildManagerEmailHtml(
        string mechanicName, string orderRef, string rows, int count)
    {
        return $"""
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8"/></head>
        <body style="font-family:Arial,sans-serif;background:#f4f4f4;margin:0;padding:0">
          <div style="max-width:680px;margin:32px auto;background:#fff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,0.1)">
            <div style="background:#1e3a5f;padding:24px 28px;display:flex;justify-content:space-between;align-items:center">
              <div>
                <div style="color:#f59e0b;font-weight:800;font-size:18px;text-transform:uppercase">Belfast Coal Mine</div>
                <div style="color:#9ca3af;font-size:12px">Equipment Maintenance Department</div>
              </div>
              <div style="background:#fef3c7;border:1px solid #f59e0b;border-radius:6px;padding:8px 14px;text-align:right">
                <div style="color:#92400e;font-weight:700;font-size:13px">ACTION REQUIRED</div>
                <div style="color:#78350f;font-size:11px">Parts Approval Needed</div>
              </div>
            </div>
            <div style="padding:28px">
              <h2 style="margin:0 0 8px;color:#1e3a5f">Parts Order for Approval</h2>
              <p style="color:#6b7280;margin:0 0 4px">Reference: <strong style="color:#1e3a5f">#{orderRef}</strong></p>
              <p style="color:#6b7280;margin:0 0 20px">Requested by: <strong style="color:#1e3a5f">{mechanicName}</strong></p>
              <p style="margin:0 0 16px;background:#fef3c7;border-left:4px solid #f59e0b;padding:10px 14px;border-radius:0 6px 6px 0">
                ⚠ The mechanic has identified <strong>{count} part(s)</strong> required to complete ongoing repairs.
                Please review the list below and the attached PDF, then approve or arrange procurement.
              </p>
              <table style="width:100%;border-collapse:collapse;font-size:13px">
                <thead>
                  <tr style="background:#1e3a5f">
                    <th style="padding:10px 12px;text-align:left;color:#fff">Machine</th>
                    <th style="padding:10px 12px;text-align:left;color:#fff">Defect Item</th>
                    <th style="padding:10px 12px;text-align:left;color:#fff">Part Required</th>
                    <th style="padding:10px 12px;text-align:left;color:#fff">Part #</th>
                  </tr>
                </thead>
                <tbody>{rows}</tbody>
              </table>
              <div style="margin-top:20px;padding:16px;background:#f0f4f8;border-radius:6px;font-size:13px;color:#374151">
                📎 A printable parts order PDF is attached to this email for your records and sign-off.
              </div>
            </div>
            <div style="padding:16px 28px;background:#f9fafb;border-top:1px solid #e5e7eb;font-size:11px;color:#9ca3af">
              Belfast Coal Mine · MHSA / DMR / CPS Level 8/9 Compliant · Auto-generated – do not reply
            </div>
          </div>
        </body>
        </html>
        """;
    }
}

public class OrderLineItem
{
    public string  MachineNumber { get; set; } = "";
    public string  MachineName   { get; set; } = "";
    public string  DefectItem    { get; set; } = "";
    public string  PartRequired  { get; set; } = "";
    public string? PartNumber    { get; set; }
}
