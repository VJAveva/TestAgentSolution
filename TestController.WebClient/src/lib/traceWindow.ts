import type { BuildDetailTest } from '../types/api';

/**
 * Opens the full debug trace of a failed test in a separate browser window,
 * mirroring the WPF app's standalone trace viewer. The popup renders the error
 * message, stack trace, debug trace, stdout and execution steps in a readable,
 * scrollable, monospace layout with per-section and copy-all buttons.
 *
 * All dynamic content is HTML-escaped before injection (the trace text can
 * contain markup-like characters), so the popup is safe against content-driven
 * markup injection.
 *
 * @returns true if the window opened, false if blocked by the popup blocker.
 */
export function openTraceWindow(test: BuildDetailTest): boolean {
  const dark = document.documentElement.getAttribute('data-theme') !== 'light';
  const html = buildTraceHtml(test, dark);

  // Render via a Blob URL rather than document.write — document.write into a
  // freshly opened window is unreliable (can leave a blank page), whereas
  // navigating the popup to an object URL renders deterministically.
  const url = URL.createObjectURL(new Blob([html], { type: 'text/html' }));
  const win = window.open(url, `trace-${slug(test.testName)}`, 'width=980,height=820,scrollbars=yes,resizable=yes');

  if (!win) {
    URL.revokeObjectURL(url);
    return false;
  }

  win.focus();
  // Revoke once the document has had time to load; revoking too early can abort
  // the navigation in some browsers.
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
  return true;
}

type Theme = {
  bg: string; card: string; panel: string;
  text: string; muted: string; border: string;
  red: string; redBg: string;
  accent: string; green: string; yellow: string;
};

const THEMES: { dark: Theme; light: Theme } = {
  dark: {
    bg: '#1E1E2E', card: '#313244', panel: '#181825',
    text: '#CDD6F4', muted: '#9399B2', border: '#585B70',
    red: '#F38BA8', redBg: 'rgba(243,139,168,0.08)',
    accent: '#89B4FA', green: '#A6E3A1', yellow: '#F9E2AF',
  },
  light: {
    bg: '#EEF0F6', card: '#FFFFFF', panel: '#F5F6FA',
    text: '#1C2333', muted: '#5A6172', border: '#D2D6E0',
    red: '#DC263C', redBg: 'rgba(220,38,60,0.06)',
    accent: '#2563EB', green: '#16A34A', yellow: '#B45309',
  },
};

function buildTraceHtml(test: BuildDetailTest, dark: boolean): string {
  const t = dark ? THEMES.dark : THEMES.light;
  const outcomeColor = test.outcome === 'Passed' ? t.green : test.outcome === 'Failed' ? t.red : t.yellow;

  const sections: string[] = [];

  if (test.errorMessage)
    sections.push(section('Error Message', test.errorMessage, t, true));
  if (test.stackTrace)
    sections.push(section('Stack Trace', test.stackTrace, t, false));
  if (test.debugTrace)
    sections.push(section(`Debug Trace (${test.debugTrace.length} chars)`, test.debugTrace, t, false));
  if (test.stdOut)
    sections.push(section(`Standard Output (${test.stdOut.length} chars)`, test.stdOut, t, false));

  if (test.steps && test.steps.length > 0) {
    const rows = test.steps.map(s => {
      const icon = s.outcome === 'Passed' ? '✔' : s.outcome === 'Failed' ? '✖' : '○';
      const color = s.outcome === 'Passed' ? t.green : s.outcome === 'Failed' ? t.red : t.yellow;
      const err = s.errorMessage ? ` — <span style="color:${t.red}">${esc(s.errorMessage)}</span>` : '';
      return `<div class="step"><span style="color:${color}">${icon}</span> ${esc(s.stepName)}${err}</div>`;
    }).join('');
    sections.push(`
      <section class="card">
        <div class="card-head"><h2>Execution Steps (${test.steps.length})</h2></div>
        <div class="steps">${rows}</div>
      </section>`);
  }

  if (sections.length === 0)
    sections.push(`<section class="card"><div class="empty">No trace details are available for this test.</div></section>`);

  const meta = [
    test.className && `Class: ${esc(test.className)}`,
    test.useCase && `Use Case: ${esc(test.useCase)}`,
    test.durationText && `Duration: ${esc(test.durationText)}`,
    test.trxFile && `TRX: ${esc(test.trxFile)}`,
  ].filter(Boolean).join(' &nbsp;·&nbsp; ');

  return `<!DOCTYPE html>
<html lang="en" data-theme="${dark ? 'dark' : 'light'}">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Trace — ${esc(test.testName)}</title>
<style>
  * { box-sizing: border-box; }
  body { margin: 0; background: ${t.bg}; color: ${t.text};
         font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; font-size: 13px; }
  header { position: sticky; top: 0; z-index: 10; background: ${t.panel};
           border-bottom: 1px solid ${t.border}; padding: 14px 20px; }
  .title { display: flex; align-items: center; gap: 10px; }
  .badge { font-size: 11px; font-weight: 700; text-transform: uppercase;
           padding: 2px 8px; border-radius: 999px; border: 1px solid ${outcomeColor};
           color: ${outcomeColor}; }
  h1 { font-size: 15px; margin: 0; font-weight: 600; word-break: break-all; }
  .meta { color: ${t.muted}; font-size: 11px; margin-top: 6px; }
  .toolbar { margin-top: 10px; }
  main { padding: 16px 20px 40px; }
  .card { background: ${t.card}; border: 1px solid ${t.border}; border-radius: 8px;
          margin-bottom: 16px; overflow: hidden; }
  .card-head { display: flex; align-items: center; justify-content: space-between;
               padding: 8px 12px; border-bottom: 1px solid ${t.border}; }
  h2 { font-size: 11px; text-transform: uppercase; letter-spacing: .06em;
       margin: 0; color: ${t.muted}; font-weight: 700; }
  .card.error h2 { color: ${t.red}; }
  .card.error { border-color: ${t.red}; background: ${t.redBg}; }
  pre { margin: 0; padding: 12px; white-space: pre-wrap; word-break: break-word;
        font-family: 'Cascadia Code', 'Consolas', 'Courier New', monospace;
        font-size: 12.5px; line-height: 1.55; overflow-x: auto; }
  .card.error pre { color: ${t.red}; }
  .steps { padding: 8px 12px; }
  .step { font-family: 'Cascadia Code', 'Consolas', monospace; font-size: 12px;
          padding: 3px 0; border-bottom: 1px solid ${t.border}33; }
  .step:last-child { border-bottom: none; }
  button { cursor: pointer; font-size: 11px; font-weight: 600; border-radius: 6px;
           border: 1px solid ${t.border}; background: transparent; color: ${t.text};
           padding: 5px 12px; }
  button:hover { background: ${t.accent}22; border-color: ${t.accent}; }
  .copied { color: ${t.green}; border-color: ${t.green}; }
  .empty { padding: 20px; color: ${t.muted}; text-align: center; }
</style>
</head>
<body>
<header>
  <div class="title">
    <span class="badge">${esc(test.outcome)}</span>
    <h1>${esc(test.testName)}</h1>
  </div>
  ${meta ? `<div class="meta">${meta}</div>` : ''}
  <div class="toolbar"><button id="copyAll">Copy All</button></div>
</header>
<main>
  ${sections.join('\n')}
</main>
<script>
  function copyText(text, btn, label) {
    navigator.clipboard.writeText(text).then(function () {
      var old = btn.textContent;
      btn.textContent = label || 'Copied';
      btn.classList.add('copied');
      setTimeout(function () { btn.textContent = old; btn.classList.remove('copied'); }, 1500);
    });
  }
  document.querySelectorAll('button[data-copy]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      var pre = btn.closest('.card').querySelector('pre');
      if (pre) copyText(pre.textContent, btn);
    });
  });
  var all = document.getElementById('copyAll');
  if (all) all.addEventListener('click', function () {
    var parts = [];
    document.querySelectorAll('.card').forEach(function (c) {
      var h = c.querySelector('h2'); var p = c.querySelector('pre');
      if (h && p) parts.push('=== ' + h.textContent + ' ===\\n' + p.textContent);
    });
    copyText(parts.join('\\n\\n'), all, 'Copied All');
  });
</script>
</body>
</html>`;
}

function section(title: string, body: string, t: Theme, isError: boolean): string {
  return `
    <section class="card${isError ? ' error' : ''}">
      <div class="card-head">
        <h2>${esc(title)}</h2>
        <button data-copy>Copy</button>
      </div>
      <pre>${esc(body)}</pre>
    </section>`;
}

function esc(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function slug(s: string): string {
  return s.replace(/[^a-z0-9]+/gi, '-').toLowerCase().slice(0, 60);
}
