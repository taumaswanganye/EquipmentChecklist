using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using EquipmentChecklist.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Controllers.Api;

/// <summary>
/// Inbound webhook endpoint(s) for external integrations (SAP PM,
/// Pragma On Key, Maximo). When the external CMMS marks a work order
/// as closed, it POSTs back to this controller and the corresponding
/// <see cref="DefectOrder"/> on our side is updated.
///
/// <para>Auth is by shared API key — the external CMMS must send the
/// configured <c>Sap.ApiKey</c> (or <c>Integrations.InboundKey</c>) in
/// the <c>X-Integration-Key</c> header. Designed to match SAP's typical
/// outbound callback config (header-based auth, JSON body, single POST).</para>
///
/// <para>Per security: this controller is OUTSIDE the JWT-auth pipeline
/// because SAP isn't going to acquire user JWTs. Anonymous + shared-key.
/// Keep the key in <c>AppSettings</c> with <c>IsSecret = true</c> so it's
/// masked in the admin UI and audit log.</para>
/// </summary>
[ApiController]
[Route("api/integrations")]
[AllowAnonymous]
public class IntegrationsController : ControllerBase
{
    private readonly ApplicationDbContext  _db;
    private readonly ConfigurationService  _config;
    private readonly AuditService          _audit;
    private readonly ILogger<IntegrationsController> _log;

    private const string IntegrationKeyHeader = "X-Integration-Key";

    public IntegrationsController(
        ApplicationDbContext db,
        ConfigurationService config,
        AuditService audit,
        ILogger<IntegrationsController> log)
    {
        _db     = db;
        _config = config;
        _audit  = audit;
        _log    = log;
    }

    /// <summary>
    /// SAP PM (or any CMMS) calls this when a mirrored work order is
    /// closed. We look up the corresponding DefectOrder by its Id (sent
    /// back as the <c>externalReference</c> field), mark it Completed,
    /// and write an audit row so the closure is traceable.
    /// </summary>
    [HttpPost("sap/work-order-closed")]
    public async Task<IActionResult> SapWorkOrderClosed(
        [FromBody] SapWorkOrderClosedRequest req,
        CancellationToken ct)
    {
        if (!await IsKeyValidAsync())
            return Unauthorized(new { error = "Invalid or missing X-Integration-Key header." });

        if (req == null || req.DefectOrderId <= 0)
            return BadRequest(new { error = "defectOrderId is required." });

        var defect = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .FirstOrDefaultAsync(d => d.Id == req.DefectOrderId, ct);

        if (defect == null)
            return NotFound(new { error = $"Defect order {req.DefectOrderId} not found." });

        if (defect.RepairStatus == RepairStatus.Completed)
        {
            // Idempotent: SAP can re-fire the callback safely. Common when
            // their workflow steps re-emit the closure event.
            return Ok(new { ok = true, alreadyCompleted = true, defectId = defect.Id });
        }

        defect.RepairStatus       = RepairStatus.Completed;
        defect.ResolvedAt         = DateTime.UtcNow;
        defect.ResolutionNotes    = string.IsNullOrWhiteSpace(req.ResolutionNotes)
                                    ? $"Closed by SAP PM (work order {req.ExternalWorkOrderId})."
                                    : req.ResolutionNotes;
        await _db.SaveChangesAsync(ct);

        // Audit the inbound closure so DMR can trace it.
        await _audit.LogAsync(
            "defect.closed.via_integration",
            targetType: "DefectOrder",
            targetId:   defect.Id,
            payload:    new {
                source           = "SAP PM",
                externalWorkOrderId = req.ExternalWorkOrderId,
                resolutionNotes  = defect.ResolutionNotes,
                closedAtUtc      = defect.ResolvedAt
            },
            ct: ct);

        _log.LogInformation(
            "Defect #{Id} closed via SAP PM (external WO: {ExtId})",
            defect.Id, req.ExternalWorkOrderId);

        return Ok(new { ok = true, defectId = defect.Id, closedAt = defect.ResolvedAt });
    }

    /// <summary>
    /// Validate the inbound shared-key header against the configured
    /// integration key. Falls back to <c>Sap.ApiKey</c> if the dedicated
    /// inbound key isn't set — most deployments will share the same key
    /// in both directions, and configuring two keys is a foot-gun.
    /// </summary>
    private async Task<bool> IsKeyValidAsync()
    {
        var presented = Request.Headers[IntegrationKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(presented)) return false;

        var configured = await _config.GetAsync("Integrations.InboundKey")
                       ?? await _config.GetAsync("Sap.ApiKey");
        if (string.IsNullOrWhiteSpace(configured)) return false;

        // Constant-time comparison — header values are small but it's good
        // hygiene to not leak length / prefix via early-exit comparison.
        return System.Security.Cryptography.CryptographicOperations
            .FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(presented),
                System.Text.Encoding.UTF8.GetBytes(configured));
    }

    /// <summary>Inbound payload from SAP PM's work-order-closed callback.</summary>
    public class SapWorkOrderClosedRequest
    {
        /// <summary>Our defect-order Id, echoed back from the original outbound
        /// payload's <c>externalReference</c> field.</summary>
        public long    DefectOrderId       { get; set; }
        public string? ExternalWorkOrderId { get; set; }
        public string? ResolutionNotes     { get; set; }
        public DateTime? ClosedAtUtc       { get; set; }
    }
}
