using System.Net;
using System.Net.Mail;
using System.Net.Mime;

namespace EquipmentChecklist.Services;

public class EmailService
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<EmailService> _log;
    private readonly MineSettings _mine;

    public EmailService(
        IConfiguration cfg,
        ILogger<EmailService> log,
        Microsoft.Extensions.Options.IOptions<MineSettings> mine)
    {
        _cfg  = cfg;
        _log  = log;
        _mine = mine.Value;
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

    private string BuildMechanicEmailHtml(
        string toName, string orderRef, string rows, int count)
    {
        return $"""
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8"/></head>
        <body style="font-family:Arial,sans-serif;background:#f4f4f4;margin:0;padding:0">
          <div style="max-width:620px;margin:32px auto;background:#fff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,0.1)">
            <div style="background:#1e3a5f;padding:24px 28px">
              <div style="color:#f59e0b;font-weight:800;font-size:18px;text-transform:uppercase">{_mine.Name}</div>
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
              {_mine.Name} · {_mine.ComplianceText} · Auto-generated – do not reply
            </div>
          </div>
        </body>
        </html>
        """;
    }

    // ── Send rejection notification to mechanic ───────────────────────────────
    /// <summary>
    /// Fires when a supervisor rejects a submission. The assigned mechanic
    /// gets a structured email with the reason, machine details, and the
    /// number of defect orders that just landed in their queue.
    /// </summary>
    /// <returns>true if at least one mail send succeeded; false if email is not
    /// configured or every send failed. Email failures must never break the
    /// reject API call — callers should not throw on a false return.</returns>
    public async Task<bool> SendRejectionNotificationAsync(
        string mechanicEmail,
        string mechanicName,
        string operatorName,
        string machineNumber,
        string machineName,
        string reason,
        int defectCount,
        string supervisorName)
    {
        var smtp     = _cfg["Email:SmtpHost"]    ?? "smtp.gmail.com";
        var port     = int.Parse(_cfg["Email:SmtpPort"] ?? "587");
        var user     = _cfg["Email:Username"]    ?? "";
        var pass     = _cfg["Email:Password"]    ?? "";
        var from     = _cfg["Email:From"]        ?? user;
        var fromName = _cfg["Email:FromName"]    ?? "Belfast Equipment System";

        if (string.IsNullOrEmpty(user))
        {
            _log.LogWarning("Email not configured — skipping rejection email. Set Email:* in appsettings.json");
            return false;
        }
        if (string.IsNullOrWhiteSpace(mechanicEmail))
        {
            _log.LogWarning("Mechanic has no email address — skipping rejection notification.");
            return false;
        }

        var body = BuildRejectionEmailHtml(
            mechanicName, operatorName, machineNumber, machineName,
            reason, defectCount, supervisorName);

        try
        {
            using var client   = new SmtpClient(smtp, port);
            client.EnableSsl   = true;
            client.Credentials = new NetworkCredential(user, pass);

            var msg = new MailMessage
            {
                From       = new MailAddress(from, fromName),
                Subject    = $"[Belfast] NO-GO · {machineNumber} rejected — {defectCount} defect(s) assigned to you",
                Body       = body,
                IsBodyHtml = true,
            };
            msg.To.Add(new MailAddress(mechanicEmail, mechanicName));

            await client.SendMailAsync(msg);
            _log.LogInformation("Rejection notification sent to mechanic {Email} for machine {Machine}",
                mechanicEmail, machineNumber);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to send rejection notification to {Email}", mechanicEmail);
            return false;
        }
    }

    private string BuildRejectionEmailHtml(
        string mechanicName, string operatorName, string machineNumber, string machineName,
        string reason, int defectCount, string supervisorName)
    {
        var safeReason = System.Net.WebUtility.HtmlEncode(reason);
        return $"""
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8"/></head>
        <body style="font-family:Arial,sans-serif;background:#f4f4f4;margin:0;padding:0">
          <div style="max-width:640px;margin:32px auto;background:#fff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,0.1)">
            <div style="background:#7f1d1d;padding:24px 28px">
              <div style="color:#fef3c7;font-weight:800;font-size:18px;text-transform:uppercase">Submission Rejected · Machine NO-GO</div>
              <div style="color:#fecaca;font-size:12px">{_mine.Name} · Equipment Checklist System</div>
            </div>
            <div style="padding:28px">
              <h2 style="margin:0 0 8px;color:#1e3a5f">Hi {mechanicName},</h2>
              <p style="margin:0 0 16px;color:#374151">
                Supervisor <strong>{supervisorName}</strong> has rejected an operator submission and
                immobilised the machine. <strong>{defectCount} defect order(s)</strong> have been
                created and assigned to you.
              </p>

              <table style="width:100%;border-collapse:collapse;font-size:13px;margin-bottom:18px">
                <tr style="background:#f0f4f8">
                  <td style="padding:10px 12px;font-weight:600;width:35%">Machine</td>
                  <td style="padding:10px 12px"><strong>{machineNumber}</strong> · {machineName}</td>
                </tr>
                <tr>
                  <td style="padding:10px 12px;font-weight:600;border-top:1px solid #e5e7eb">Operator</td>
                  <td style="padding:10px 12px;border-top:1px solid #e5e7eb">{operatorName}</td>
                </tr>
                <tr style="background:#f0f4f8">
                  <td style="padding:10px 12px;font-weight:600">Rejected by</td>
                  <td style="padding:10px 12px">{supervisorName}</td>
                </tr>
                <tr>
                  <td style="padding:10px 12px;font-weight:600;border-top:1px solid #e5e7eb">Defect orders</td>
                  <td style="padding:10px 12px;border-top:1px solid #e5e7eb;color:#b91c1c"><strong>{defectCount}</strong> assigned to you</td>
                </tr>
              </table>

              <div style="margin:18px 0;padding:14px 16px;background:#fef3c7;border-left:4px solid #f59e0b;border-radius:0 6px 6px 0">
                <div style="font-weight:700;color:#92400e;margin-bottom:4px">Supervisor's reason</div>
                <div style="color:#78350f;font-style:italic">{safeReason}</div>
              </div>

              <div style="margin-top:24px;padding:16px;background:#fef2f2;border:1px solid #fecaca;border-radius:6px">
                <strong style="color:#b91c1c">⚠ Machine is immobilised</strong>
                <p style="margin:6px 0 0;color:#7f1d1d;font-size:13px">
                  Open the mobile app — <strong>My Defects</strong> — to review each defect, request parts, and close repairs.
                </p>
              </div>
            </div>
            <div style="padding:16px 28px;background:#f9fafb;border-top:1px solid #e5e7eb;font-size:11px;color:#9ca3af">
              {_mine.Name} · {_mine.ComplianceText} · Auto-generated — do not reply
            </div>
          </div>
        </body>
        </html>
        """;
    }

    private string BuildManagerEmailHtml(
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
                <div style="color:#f59e0b;font-weight:800;font-size:18px;text-transform:uppercase">{_mine.Name}</div>
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
              {_mine.Name} · {_mine.ComplianceText} · Auto-generated – do not reply
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
