// On-device PDF generation for offline checklist submissions.
//
// Why: When operators submit a checklist underground (no signal), the server
// can't render the canonical PSIC PDF. We build a close visual match here in
// JS using jsPDF + jspdf-autotable, hand the base64 bytes back to .NET, and
// cache them in SQLite keyed by the submission's LocalId. The next time the
// operator (or supervisor) taps "PDF" on that submission, we serve the local
// copy until the server has rendered its own — at which point the server
// version takes over.
//
// Layout (mirrors the Kolomela / Belfast Coal Mine PSIC sheet):
//   1. Dark header band with mine name + sub-title
//   2. Title row: "PRE-SHIFT INSPECTION CHECKLIST: <machine type>"
//   3. Info row: Date | Time | Shift | Odometer | legend (NO-GO red, GO-BUT yellow)
//   4. Mine-name banner
//   5. SLAM NR + Vehicle ID strip
//   6. Two-column item grid with:
//        - red row background  → critical / NO-GO items
//        - yellow row background → warning category items (TODO when categories exist)
//        - white default
//        - P (Pass / In Order) / O (Not in Order) in the status cell
//        - sub-line under defective items showing the operator's note
//   7. Operator + supervisor signature blocks at the bottom (when present)
//
// Dependencies (must be loaded before this file in index.html):
//   - jspdf.umd.min.js
//   - jspdf.plugin.autotable.min.js

window.PdfGenerator = (function () {

  /**
   * Cheap probe used by .NET to detect "jsPDF isn't loaded" up front, so the
   * UI can surface a clear "vendor the libraries" message instead of silently
   * falling through to the un-synced placeholder.
   * @returns {boolean}
   */
  function isReady() {
    return !!(window.jspdf && window.jspdf.jsPDF);
  }


  /**
   * Build the checklist PDF and return the bytes as a base64 string (no
   * `data:application/pdf;base64,` prefix). .NET will turn the bytes around
   * and either show them in the PdfViewer iframe or persist them in SQLite.
   *
   * @param {object} payload - All the data needed to render. Shape:
   *   {
   *     mineName: "BELFAST COAL MINE",
   *     subtitle: "Pre-Shift Inspection Checklist",
   *     machineType: "GRADER",
   *     machineNumber: "GRD-001",
   *     machineName: "Caterpillar 14M",
   *     date: "29 May 2026",
   *     time: "18:18",
   *     shift: "Day (A)",
   *     odometer: "1222",
   *     slamNr: "" | "12345",
   *     items: [
   *       { name: "SEAT BELT", status: "P"|"O", isCritical: true,
   *         isWarning: false, note: "No working" }
   *     ],
   *     operatorName: "Tau Maswanganye",
   *     employeeNumber: "EMP-001",
   *     operatorSignature: "data:image/png;base64,...",
   *     supervisorName: null,
   *     supervisorSignature: null,
   *     remarks: "" | "Steering pulls left"
   *   }
   */
  function buildChecklist(payload) {
    if (!window.jspdf || !window.jspdf.jsPDF) {
      throw new Error("jsPDF is not loaded. Make sure jspdf.umd.min.js is in wwwroot/lib/.");
    }
    const { jsPDF } = window.jspdf;
    const doc = new jsPDF({ unit: 'mm', format: 'a4', compress: true });

    const pageW   = doc.internal.pageSize.getWidth();   // 210
    const margin  = 8;
    const innerW  = pageW - margin * 2;                 // 194

    let y = margin;

    // ──────────────────────────────────────────────────────────────────────
    // 1 · Dark header band
    // ──────────────────────────────────────────────────────────────────────
    doc.setFillColor(13, 23, 41);             // #0d1729 (deep navy)
    doc.rect(margin, y, innerW, 11, 'F');
    doc.setTextColor(255, 255, 255);
    doc.setFont('helvetica', 'bold').setFontSize(11);
    doc.text(payload.mineName || 'MINE', margin + 4, y + 5);
    doc.setFont('helvetica', 'normal').setFontSize(8);
    doc.setTextColor(180, 199, 224);
    doc.text(payload.subtitle || 'Pre-Shift Inspection Checklist', margin + 4, y + 9.5);
    // Right-aligned mine name echo (like the sample)
    doc.setFont('helvetica', 'bold').setFontSize(9).setTextColor(255, 255, 255);
    doc.text(payload.mineName || '', margin + innerW - 4, y + 7, { align: 'right' });
    y += 11;

    // ──────────────────────────────────────────────────────────────────────
    // 2 · Title row
    // ──────────────────────────────────────────────────────────────────────
    doc.setFillColor(232, 240, 248);
    doc.rect(margin, y, innerW, 8, 'F');
    doc.setDrawColor(180, 199, 224).rect(margin, y, innerW, 8);
    doc.setFont('helvetica', 'bold').setFontSize(11).setTextColor(30, 58, 95);
    doc.text(
      `PRE-SHIFT INSPECTION CHECKLIST: ${(payload.machineType || '').toUpperCase()}`,
      margin + innerW / 2,
      y + 5.5,
      { align: 'center' }
    );
    y += 8;

    // ──────────────────────────────────────────────────────────────────────
    // 3 · Info row — Date | Time | Shift | Odometer | Legend
    // ──────────────────────────────────────────────────────────────────────
    const infoH = 22;
    doc.setDrawColor(200, 210, 222).setLineWidth(0.2);

    const infoCols = [
      { label: 'Date',                  value: payload.date     || '—', w: 28 },
      { label: 'Time',                  value: payload.time     || '—', w: 18 },
      { label: 'Shift',                 value: payload.shift    || '—', w: 22 },
      { label: 'Odometer Reading (km)', value: payload.odometer || '—', w: 40 },
    ];

    let cx = margin;
    infoCols.forEach(col => {
      doc.rect(cx, y, col.w, infoH);
      doc.setFont('helvetica', 'normal').setFontSize(7).setTextColor(120, 130, 145);
      doc.text(col.label, cx + 2, y + 4);
      doc.setFont('helvetica', 'bold').setFontSize(11).setTextColor(30, 41, 59);
      doc.text(col.value, cx + 2, y + 13);
      cx += col.w;
    });

    // Legend on the right
    const legW = innerW - (cx - margin);
    doc.rect(cx, y, legW, infoH);
    doc.setFontSize(6.5).setFont('helvetica', 'normal').setTextColor(120, 130, 145);
    doc.text('The codes used to indicate deviations from standards:', cx + 2, y + 4);

    // NO-GO swatch
    const swX = cx + 2;
    const swW = 14;
    doc.setFillColor(220, 38, 38).rect(swX, y + 6.5, swW, 4.5, 'F');
    doc.setTextColor(255, 255, 255).setFont('helvetica', 'bold').setFontSize(8);
    doc.text('NO-GO', swX + swW / 2, y + 9.7, { align: 'center' });
    doc.setTextColor(60, 60, 60).setFont('helvetica', 'normal').setFontSize(6.5);
    doc.text(
      'The TMM may not be operated, except to effect repairs.',
      swX + swW + 2, y + 8, { maxWidth: legW - swW - 6 }
    );
    doc.text(
      'Defect(s) must be rectified before TMM returns to production.',
      swX + swW + 2, y + 10.5, { maxWidth: legW - swW - 6 }
    );

    // GO-BUT swatch
    doc.setFillColor(245, 158, 11).rect(swX, y + 13.5, swW, 4.5, 'F');
    doc.setTextColor(255, 255, 255).setFont('helvetica', 'bold').setFontSize(8);
    doc.text('GO-BUT', swX + swW / 2, y + 16.7, { align: 'center' });
    doc.setTextColor(60, 60, 60).setFont('helvetica', 'normal').setFontSize(6.5);
    doc.text(
      'TMM safe to operate; defect must be attended to by an',
      swX + swW + 2, y + 15, { maxWidth: legW - swW - 6 }
    );
    doc.text(
      'authorised artisan within the shift, under conditions',
      swX + swW + 2, y + 17.3, { maxWidth: legW - swW - 6 }
    );

    y += infoH;

    // ──────────────────────────────────────────────────────────────────────
    // 4 · Mine-name banner
    // ──────────────────────────────────────────────────────────────────────
    doc.setFillColor(245, 248, 252);
    doc.rect(margin, y, innerW, 6, 'F');
    doc.setDrawColor(200, 210, 222).rect(margin, y, innerW, 6);
    doc.setFont('helvetica', 'bold').setFontSize(9).setTextColor(30, 58, 95);
    doc.text(payload.mineName || '', margin + innerW / 2, y + 4.2, { align: 'center' });
    y += 6;

    // ──────────────────────────────────────────────────────────────────────
    // 5 · SLAM NR + Vehicle ID strip
    // ──────────────────────────────────────────────────────────────────────
    const stripH = 11;
    const slamW  = innerW * 0.42;
    const vehW   = innerW - slamW;

    doc.rect(margin, y, slamW, stripH);
    doc.setFont('helvetica', 'normal').setFontSize(7).setTextColor(120, 130, 145);
    doc.text('SLAM NR', margin + 2, y + 3.5);
    doc.setFont('helvetica', 'bold').setFontSize(10).setTextColor(30, 41, 59);
    doc.text(payload.slamNr || '—', margin + 2, y + 9);

    doc.rect(margin + slamW, y, vehW, stripH);
    doc.setFont('helvetica', 'normal').setFontSize(7).setTextColor(120, 130, 145);
    doc.text('Vehicle ID / Equipment #', margin + slamW + 2, y + 3.5);
    doc.setFont('helvetica', 'bold').setFontSize(10).setTextColor(30, 41, 59);
    doc.text(payload.machineNumber || '—', margin + slamW + 2, y + 9);
    y += stripH;

    // ──────────────────────────────────────────────────────────────────────
    // 6 · Two-column item grid
    // ──────────────────────────────────────────────────────────────────────
    // Header row
    const colW   = innerW / 2;
    const headH  = 7;
    doc.setFillColor(245, 248, 252).rect(margin, y, innerW, headH, 'F');
    doc.setDrawColor(200, 210, 222).rect(margin, y, innerW, headH);
    doc.setFont('helvetica', 'bold').setFontSize(8).setTextColor(30, 58, 95);
    doc.text('Description / Activity', margin + 4, y + 4.6);
    doc.setFont('helvetica', 'normal').setFontSize(7).setTextColor(80, 90, 110);
    doc.text('P · In Order        O · Not In Order',
             margin + innerW - 4, y + 4.6, { align: 'right' });
    y += headH;

    const items     = payload.items || [];
    const half      = Math.ceil(items.length / 2);
    const rowH      = 7.2;
    const statusW   = 10;        // width of the P/O column
    const bulletX   = 3;
    const labelX    = 8;
    const labelMax  = colW - statusW - labelX - 2;
    let   rowY      = y;

    // Track tallest column so we know where the grid ends
    let leftY  = y;
    let rightY = y;

    for (let i = 0; i < half; i++) {
      const left  = items[i];
      const right = items[i + half];

      // Left column
      drawItemRow(doc, left,  margin,        leftY,  colW, rowH, statusW,
                  bulletX, labelX, labelMax);
      leftY += rowHeightFor(left, rowH);

      if (right) {
        drawItemRow(doc, right, margin + colW, rightY, colW, rowH, statusW,
                    bulletX, labelX, labelMax);
        rightY += rowHeightFor(right, rowH);
      }
    }

    y = Math.max(leftY, rightY);

    // ──────────────────────────────────────────────────────────────────────
    // 7 · Remarks + signatures
    // ──────────────────────────────────────────────────────────────────────
    y += 4;
    if (payload.remarks && payload.remarks.trim().length > 0) {
      doc.setFillColor(255, 251, 235).rect(margin, y, innerW, 14, 'F');
      doc.setDrawColor(245, 158, 11).rect(margin, y, innerW, 14);
      doc.setFont('helvetica', 'bold').setFontSize(8).setTextColor(146, 64, 14);
      doc.text('OPERATOR REMARKS', margin + 3, y + 4);
      doc.setFont('helvetica', 'italic').setFontSize(8.5).setTextColor(60, 50, 30);
      doc.text(payload.remarks, margin + 3, y + 9, { maxWidth: innerW - 6 });
      y += 16;
    }

    // Signature blocks side by side
    const sigW = innerW / 2 - 2;
    const sigH = 26;

    drawSigBlock(doc, margin,         y, sigW, sigH,
                 'OPERATOR', payload.operatorName, payload.employeeNumber,
                 payload.operatorSignature);
    drawSigBlock(doc, margin + sigW + 4, y, sigW, sigH,
                 'SUPERVISOR', payload.supervisorName || '—', null,
                 payload.supervisorSignature);

    // Footer
    const fY = doc.internal.pageSize.getHeight() - 6;
    doc.setFont('helvetica', 'normal').setFontSize(6.5).setTextColor(120, 130, 145);
    doc.text(
      `${payload.mineName || ''} · MHSA / DMR / CPS Level 8/9 Compliant · Generated on-device (offline)`,
      pageW / 2, fY, { align: 'center' }
    );

    // Strip the `data:application/pdf;base64,` prefix — .NET gets just the bytes.
    const dataUri = doc.output('datauristring');
    return dataUri.substring(dataUri.indexOf(',') + 1);
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  function drawItemRow(doc, item, x, y, w, baseH, statusW, bulletX, labelX, labelMax) {
    if (!item) return;
    const h = rowHeightFor(item, baseH);

    // Background colour
    let bg = [255, 255, 255];
    if (item.isCritical) bg = [220, 38, 38];          // red
    else if (item.isWarning) bg = [254, 235, 200];     // soft yellow
    doc.setFillColor(bg[0], bg[1], bg[2]).rect(x, y, w, h, 'F');
    doc.setDrawColor(220, 225, 235).rect(x, y, w, h);

    // Bullet + name
    const text = item.isCritical ? [255, 255, 255] : [30, 41, 59];
    doc.setTextColor(text[0], text[1], text[2]);
    doc.setFont('helvetica', 'bold').setFontSize(7.5);
    doc.text('•', x + bulletX, y + 4.8);
    doc.setFont('helvetica', 'bold').setFontSize(8.5);
    doc.text((item.name || '').toUpperCase(), x + labelX, y + 4.8, { maxWidth: labelMax });

    // Defect note on a second line
    if (item.note && item.status === 'O') {
      doc.setFont('helvetica', 'italic').setFontSize(7);
      const noteColor = item.isCritical ? [255, 220, 220] : [180, 30, 30];
      doc.setTextColor(noteColor[0], noteColor[1], noteColor[2]);
      doc.text(`✗ ${item.note}`, x + labelX, y + 9.2, { maxWidth: labelMax });
    }

    // Voice memo marker — PDFs can't play audio, so we just print a small
    // tag so readers know to open the app to listen.
    if (item.hasAudio && item.status === 'O') {
      doc.setFont('helvetica', 'bold').setFontSize(6.5);
      doc.setTextColor(146, 64, 14);
      doc.text('🎤 voice memo attached', x + labelX, y + (item.note ? 12.5 : 8));
    }

    // Defect photo as a thumbnail under the note. Sits inside the same row
    // band so we don't have to reflow the two-column grid mid-page.
    if (item.photoDataUrl && item.status === 'O') {
      const photoY = y + h - PHOTO_H - 1;
      try {
        // jsPDF auto-detects JPEG vs PNG from the data URL prefix.
        doc.addImage(item.photoDataUrl, x + labelX, photoY, PHOTO_W, PHOTO_H);
      } catch (e) {
        // Some camera output (e.g. HEIC on iOS Safari) jsPDF can't decode.
        // Draw a placeholder marker so the reader knows a photo was attached
        // even though we couldn't embed it.
        doc.setDrawColor(180, 30, 30).rect(x + labelX, photoY, PHOTO_W, PHOTO_H);
        doc.setFont('helvetica', 'normal').setFontSize(6).setTextColor(180, 30, 30);
        doc.text('📷 photo attached',
                 x + labelX + PHOTO_W / 2, photoY + PHOTO_H / 2,
                 { align: 'center' });
      }
    }

    // Status cell (right side)
    const statusX = x + w - statusW;
    doc.setDrawColor(220, 225, 235).line(statusX, y, statusX, y + h);
    doc.setFont('helvetica', 'bold').setFontSize(13);
    doc.setTextColor(item.status === 'O' ? 220 : (item.isCritical ? 255 : 30),
                     item.status === 'O' ? 38  : (item.isCritical ? 255 : 41),
                     item.status === 'O' ? 38  : (item.isCritical ? 255 : 59));
    doc.text(item.status || '—', statusX + statusW / 2, y + h / 2 + 2,
             { align: 'center' });
  }

  function rowHeightFor(item, baseH) {
    if (!item) return baseH;
    let h = baseH;
    // Defect note adds one wrapped line of ~4mm
    if (item.note && item.status === 'O') h += 4;
    // Photo adds a thumbnail at the bottom of the row
    if (item.photoDataUrl && item.status === 'O') h += PHOTO_H + 2;
    return h;
  }

  // Visual size of the embedded photo inside an item row.
  const PHOTO_H = 24;  // mm
  const PHOTO_W = 32;  // mm

  function drawSigBlock(doc, x, y, w, h, label, name, empNo, sigDataUrl) {
    doc.setDrawColor(180, 199, 224).rect(x, y, w, h);
    doc.setFont('helvetica', 'bold').setFontSize(7).setTextColor(120, 130, 145);
    doc.text(label, x + 3, y + 4);

    doc.setFont('helvetica', 'bold').setFontSize(9).setTextColor(30, 41, 59);
    doc.text(name || '—', x + 3, y + 8);

    if (empNo) {
      doc.setFont('helvetica', 'normal').setFontSize(7).setTextColor(120, 130, 145);
      doc.text(`#${empNo}`, x + 3, y + 11.5);
    }

    // Signature image at the bottom of the box
    if (sigDataUrl) {
      try {
        doc.addImage(sigDataUrl, 'PNG', x + 3, y + 13, w - 6, h - 16);
      } catch (e) {
        // Some signature pads return JPEG — try JPEG as a fallback.
        try { doc.addImage(sigDataUrl, 'JPEG', x + 3, y + 13, w - 6, h - 16); }
        catch (_) { /* give up silently */ }
      }
    }
  }

  return { buildChecklist, isReady };
})();
