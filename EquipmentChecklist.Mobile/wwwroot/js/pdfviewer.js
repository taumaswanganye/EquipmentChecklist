// Small helper for inline PDF rendering inside a MAUI Blazor Hybrid WebView.
//
// Why a blob URL? The PDF endpoint is JWT-authenticated. An <iframe src="...">
// pointing directly at it would send no Authorization header, and WebView2
// won't share cookies / headers from the host page. So we fetch the bytes from
// .NET, hand the base64 string to JS, and JS rebuilds the bytes as a Blob and
// hands the embedded iframe a transient blob: URL. WebView2's built-in PDF
// viewer renders it natively — no PDF.js dependency.
//
// Always call PdfViewer.revoke(url) when the modal closes; blob URLs sit in
// memory until revoked or until the page unloads.

window.PdfViewer = (function () {
  /**
   * Build a blob: URL from a base64-encoded PDF.
   * @param {string} base64 - The PDF bytes encoded as base64 (no data: prefix).
   * @returns {string} A blob URL safe to assign to an iframe's src.
   */
  function fromBase64(base64) {
    // atob → binary string → typed array. Loop unrolled to a single Uint8Array
    // alloc for performance; PDFs over a few MB feel snappy this way.
    const binary = atob(base64);
    const len    = binary.length;
    const bytes  = new Uint8Array(len);
    for (let i = 0; i < len; i++) bytes[i] = binary.charCodeAt(i);

    const blob = new Blob([bytes], { type: 'application/pdf' });
    return URL.createObjectURL(blob);
  }

  /**
   * Release a blob URL. Call this when the modal closes so the bytes can be
   * garbage collected.
   * @param {string} url - The url returned by fromBase64.
   */
  function revoke(url) {
    if (url) URL.revokeObjectURL(url);
  }

  return { fromBase64, revoke };
})();
