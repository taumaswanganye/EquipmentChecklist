using System.Text.Json;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Models;
using Microsoft.JSInterop;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Generates a PDF for a checklist submission entirely on-device, using the
/// jsPDF + jspdf-autotable libraries vendored under <c>wwwroot/lib/</c>.
///
/// <para>Why a service rather than calling JS straight from <c>Checklist.razor</c>?
/// Because (a) the payload shape needs to come from several sources (template,
/// cached machine, form state, signed-in user) and gluing them together is
/// non-trivial, and (b) the same generator may eventually be called from a
/// follow-up "Result" page or from MySubmissions when a row is queued.
/// Centralising keeps the JSON contract with the JS in one place.</para>
///
/// <para>Returns null if anything goes wrong — callers should treat a null
/// as "no offline PDF available, fall back to placeholder".</para>
/// </summary>
public class LocalPdfService
{
    private readonly IJSRuntime         _js;
    private readonly LocalCache         _cache;
    private readonly SubmissionQueue    _queue;
    private readonly AuthService        _auth;
    private readonly MineConfigService? _mine;

    public LocalPdfService(IJSRuntime js, LocalCache cache,
                           SubmissionQueue queue, AuthService auth,
                           MineConfigService? mine = null)
    {
        _js    = js;
        _cache = cache;
        _queue = queue;
        _auth  = auth;
        _mine  = mine;
    }

    /// <summary>
    /// Last error from a failed generate attempt — surfaced to the UI so the
    /// user can see why the local PDF didn't render (jsPDF missing, etc).
    /// Cleared whenever a generate succeeds.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Has the jsPDF library actually loaded into the WebView yet? Used by
    /// the UI to swap the un-synced placeholder for a clearer "vendor the
    /// libraries" message when the vendored files are missing.
    /// </summary>
    public async Task<bool> IsAvailableAsync()
    {
        try   { return await _js.InvokeAsync<bool>("PdfGenerator.isReady"); }
        catch { return false; }
    }

    /// <summary>
    /// Look up the queued submission by its LocalId and (re)generate its PDF.
    /// Used as the fallback when <c>PdfViewer</c> opens a queued row whose
    /// cached PDF either never landed (initial generation failed) or was
    /// pruned. Returns null if the queue doesn't have a matching entry, or if
    /// any of the supporting state (template, machine, signed-in user) is
    /// missing.
    /// </summary>
    public async Task<byte[]?> GenerateFromLocalIdAsync(Guid localId, string mineName)
    {
        // Find the queue entry. PendingSubmission.LocalId is a Guid column.
        var entries = await _queue.GetAllAsync();
        var entry   = entries.FirstOrDefault(e => e.LocalId == localId);
        if (entry == null) return null;

        SyncSubmissionRequest? req;
        try { req = JsonSerializer.Deserialize<SyncSubmissionRequest>(entry.JsonPayload); }
        catch { return null; }
        if (req is null) return null;

        var machine = (await _cache.GetMachinesAsync()).FirstOrDefault(m => m.Id == req.MachineId);
        if (machine is null) return null;

        var template = await _cache.GetTemplateAsync(req.MachineId);
        if (template is null) return null;

        var user = _auth.CurrentUser;
        if (user is null) return null;

        return await GenerateAndCacheAsync(req, template, machine, user, mineName);
    }

    /// <summary>
    /// Build the JS payload from in-app state, call the JS generator, persist
    /// the bytes to SQLite keyed by <c>LocalId</c>, and return them so the
    /// caller can also show the PDF immediately if it wants.
    /// </summary>
    public async Task<byte[]?> GenerateAndCacheAsync(
        SyncSubmissionRequest    req,
        SyncTemplateDto          template,
        SyncMachineSummaryDto    machine,
        SyncUserDto              operatorUser,
        string                   mineName)
    {
        // Resolve mine-specific labels here in the instance method, then pass
        // them into the static payload builder. BuildPayload stays static
        // (no `this`) which makes it trivial to unit-test in isolation.
        var tagline = string.IsNullOrEmpty(_mine?.Current.Tagline)
            ? "Pre-Shift Inspection Checklist"
            : _mine!.Current.Tagline;
        var compliance = _mine?.Current.ComplianceText ?? "";

        var payload = BuildPayload(
            req, template, machine, operatorUser, mineName, tagline, compliance);

        // Probe first: a clear "library missing" failure is much easier to
        // debug than a generic "interop threw" one.
        if (!await IsAvailableAsync())
        {
            LastError = "jsPDF library is not loaded. Vendor jspdf.umd.min.js + " +
                        "jspdf.plugin.autotable.min.js into wwwroot/lib/ — see runbook.";
            return null;
        }

        // Call jsPDF in the WebView. Returns base64 string (no data: prefix).
        string base64;
        try
        {
            base64 = await _js.InvokeAsync<string>("PdfGenerator.buildChecklist", payload);
        }
        catch (Exception ex)
        {
            // jsPDF can also throw on weird input (e.g. an empty signature
            // data URL). Capture the message so the UI can show it.
            LastError = $"On-device PDF render failed: {ex.Message}";
            return null;
        }

        if (string.IsNullOrEmpty(base64))
        {
            LastError = "jsPDF returned empty bytes.";
            return null;
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (Exception ex)
        {
            LastError = $"Couldn't decode PDF bytes: {ex.Message}";
            return null;
        }
        LastError = null;  // success path clears stale errors

        // Persist so PdfViewer can serve it later without re-running jsPDF.
        try
        {
            await _cache.SaveLocalPdfAsync(req.LocalId, bytes,
                machine.Id, machine.MachineNumber);
        }
        catch
        {
            // Cache write failures are non-fatal — we'll still return the
            // bytes so the immediate "view PDF now" path works.
        }

        return bytes;
    }

    // ── Payload shape (must match the JSDoc on PdfGenerator.buildChecklist) ─
    private static object BuildPayload(
        SyncSubmissionRequest req,
        SyncTemplateDto       template,
        SyncMachineSummaryDto machine,
        SyncUserDto           operatorUser,
        string                mineName,
        string                subtitle,
        string                compliance)
    {
        // Index template items so we can resolve TemplateItemId → name / flags.
        var itemsByTplId = template.Items.ToDictionary(i => i.Id);

        var items = req.Items.Select(i =>
        {
            itemsByTplId.TryGetValue(i.TemplateItemId, out var tpl);
            return new
            {
                name       = tpl?.ItemName ?? $"Item #{i.TemplateItemId}",
                status     = i.Status == ItemStatus.InOrder ? "P" : "O",
                isCritical = tpl?.IsNoGoItem ?? false,
                // Warning category isn't modelled yet; placeholder for now.
                isWarning  = false,
                note       = i.Notes ?? "",
                // Optional defect photo — JS-side renderer embeds it under
                // the note as a thumbnail. Empty string when there's none.
                photoDataUrl = string.IsNullOrEmpty(i.PhotoBase64)
                    ? ""
                    : $"data:{i.PhotoMimeType ?? "image/jpeg"};base64,{i.PhotoBase64}",
                // PDFs can't play audio. The JS renderer prints a small
                // "🎤 voice memo attached" tag so anyone reading the printout
                // knows to open the app to listen.
                hasAudio     = !string.IsNullOrEmpty(i.AudioBase64)
            };
        }).ToList();

        var submitted = req.SubmittedAt == default ? DateTime.UtcNow : req.SubmittedAt;

        // subtitle + compliance are resolved by the caller from MineConfigService
        // (see GenerateAndCacheAsync). Keeping them as parameters lets this
        // method stay static and trivially testable.

        return new
        {
            mineName,
            subtitle,
            compliance,
            machineType   = machine.TypeDisplay,
            machineNumber = machine.MachineNumber,
            machineName   = machine.MachineName,
            date          = submitted.ToString("dd MMM yyyy"),
            time          = submitted.ToString("HH:mm"),
            shift         = ShiftLabel(req.Shift),
            odometer      = req.KmOrHourMeter?.ToString() ?? "—",
            slamNr        = (string?)null,
            items,
            operatorName        = operatorUser.FullName,
            employeeNumber      = operatorUser.EmployeeNumber,
            operatorSignature   = req.OperatorSignature,
            supervisorName      = (string?)null,
            supervisorSignature = (string?)null,
            remarks             = req.OperatorRemarks ?? ""
        };
    }

    private static string ShiftLabel(Shift s) => s switch
    {
        Shift.Day       => "Day (A)",
        Shift.Afternoon => "Afternoon (B)",
        Shift.Night     => "Night (C)",
        _               => "—"
    };
}
