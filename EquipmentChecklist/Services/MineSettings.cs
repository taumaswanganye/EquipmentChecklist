namespace EquipmentChecklist.Services;

/// <summary>
/// Site-specific branding the server uses to fill in PDF templates, email
/// bodies, and the read-only <c>GET /api/sync/mine</c> endpoint the mobile
/// app reads on sign-in.
///
/// <para>Bound from <c>appsettings.json</c>:</para>
/// <code>
/// "Mine": {
///   "Name":        "Belfast Coal Mine",
///   "ShortName":   "BELFAST",
///   "Tagline":     "Digital Equipment Checklist System",
///   "ComplianceText": "MHSA / DMR / CPS Level 8/9 Compliant"
/// }
/// </code>
///
/// <para>If the section is missing, defaults fire so the app still works
/// out of the box — operators on a freshly cloned repo aren't blocked.</para>
/// </summary>
public class MineSettings
{
    /// <summary>Full display name, e.g. "Belfast Coal Mine".</summary>
    public string Name           { get; set; } = "Pre-Checklist Mine";

    /// <summary>Short label for headers, e.g. "BELFAST".</summary>
    public string ShortName      { get; set; } = "MINE";

    /// <summary>Sub-line shown in the PDF + splash, e.g. "Pre-Shift Inspection".</summary>
    public string Tagline        { get; set; } = "Pre-Shift Inspection Checklist";

    /// <summary>Compliance footer text rendered in PDFs and email footers.</summary>
    public string ComplianceText { get; set; } = "MHSA / DMR / CPS Level 8/9 Compliant";
}
