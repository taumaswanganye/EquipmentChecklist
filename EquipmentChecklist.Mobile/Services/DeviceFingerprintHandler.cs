namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// HttpMessageHandler that adds the <c>X-Device-Fingerprint</c> header to
/// every outgoing API request. The server requires the header for both
/// the login endpoint and every authenticated call, so adding it once at
/// the handler level beats every ApiClient method threading it through.
/// </summary>
public class DeviceFingerprintHandler : DelegatingHandler
{
    private readonly DeviceFingerprint _fingerprint;

    public DeviceFingerprintHandler(DeviceFingerprint fingerprint)
    {
        _fingerprint = fingerprint;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var fp = await _fingerprint.GetAsync();
            if (!string.IsNullOrWhiteSpace(fp))
                request.Headers.TryAddWithoutValidation("X-Device-Fingerprint", fp);
        }
        catch
        {
            // Don't block the request on a fingerprint-resolution failure.
            // The server will reject the call with device_missing_fingerprint
            // and the mobile UI will surface that — better than hanging.
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
