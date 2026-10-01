/* Local-only terminal host. Remote output is data passed to write(), never HTML. */
const terminal = new Terminal({cols:80, rows:24, fontSize:13, fontFamily:'monospace',
  scrollback:2000, theme:{background:'#101820', foreground:'#f2f5f7'}});
const fit = new FitAddon.FitAddon();
terminal.loadAddon(fit);
terminal.open(document.getElementById('terminal'));
// Keep editing/paste in Compose, but leave VT query responses enabled.
terminal.attachCustomKeyEventHandler(() => false);
terminal.textarea.readOnly = true;
terminal.textarea.setAttribute('inputmode', 'none');
terminal.element.addEventListener('paste', event => {
  event.preventDefault();
  event.stopImmediatePropagation();
}, true);
// xterm's desktop scrollbar does not implement finger dragging. Translate gestures
// into its public scroll API, keeping fractional rows so slow drags remain accurate.
const latest = document.getElementById('latest');
const root = document.getElementById('terminal');
let gesture = null, inertia = 0, remainder = 0;
function updateLatest() {
  latest.hidden = terminal.buffer.active.viewportY >= terminal.buffer.active.baseY;
}
terminal.onScroll(updateLatest);
latest.addEventListener('click', () => {
  cancelAnimationFrame(inertia);
  terminal.scrollToBottom();
  updateLatest();
});
function scrollPixels(pixels) {
  const height = terminal.element.querySelector('.xterm-screen').getBoundingClientRect().height / terminal.rows;
  if (!height) return;
  remainder += pixels / height;
  const lines = Math.trunc(remainder);
  if (lines) {
    terminal.scrollLines(lines);
    remainder -= lines;
    updateLatest();
  }
}
root.addEventListener('touchstart', event => {
  cancelAnimationFrame(inertia);
  remainder = 0;
  gesture = event.touches.length === 1 ? {
    startY:event.touches[0].clientY, y:event.touches[0].clientY,
    time:performance.now(), velocity:0, dragging:false
  } : null;
  if (gesture) event.stopImmediatePropagation();
}, {capture:true, passive:true});
root.addEventListener('touchmove', event => {
  if (!gesture || event.touches.length !== 1) { gesture = null; return; }
  const y = event.touches[0].clientY, now = performance.now();
  if (!gesture.dragging && Math.abs(y - gesture.startY) < 6) return;
  gesture.dragging = true;
  event.preventDefault();
  event.stopImmediatePropagation();
  const pixels = gesture.y - y;
  gesture.velocity = 0.6 * gesture.velocity + 0.4 * Math.max(-2, Math.min(2, pixels / Math.max(1, now - gesture.time)));
  scrollPixels(pixels);
  gesture.y = y;
  gesture.time = now;
}, {capture:true, passive:false});
root.addEventListener('touchend', event => {
  if (!gesture?.dragging) { gesture = null; return; }
  event.preventDefault();
  event.stopImmediatePropagation();
  let velocity = performance.now() - gesture.time < 100 ? gesture.velocity : 0;
  let time = performance.now();
  gesture = null;
  function coast(now) {
    const elapsed = Math.min(32, now - time);
    time = now;
    scrollPixels(velocity * elapsed);
    velocity *= Math.pow(0.92, elapsed / 16);
    if (Math.abs(velocity) > 0.02) inertia = requestAnimationFrame(coast);
  }
  inertia = requestAnimationFrame(coast);
}, {capture:true, passive:false});
root.addEventListener('touchcancel', () => { gesture = null; cancelAnimationFrame(inertia); }, {capture:true});
let timer;
let connected = false;
let lastSize = '';
function scheduleFit() {
  clearTimeout(timer);
  timer = setTimeout(() => {
    const size = fit.proposeDimensions();
    if (!size || size.cols < 2 || size.rows < 1) return;
    const cols = Math.min(500, size.cols), rows = Math.min(200, size.rows);
    const atBottom = terminal.buffer.active.viewportY >= terminal.buffer.active.baseY;
    terminal.resize(cols, rows);
    if (atBottom) terminal.scrollToBottom();
    updateLatest();
    const key = `${cols}:${rows}`;
    if (connected && key !== lastSize) {
      lastSize = key;
      NativeTerminal.resize(cols, rows);
    }
  }, 200);
}
new ResizeObserver(scheduleFit).observe(document.getElementById('terminal'));
// Includes automatic VT responses such as cursor-position reports needed by Windows.
terminal.onData(data => { if (connected) NativeTerminal.send(data); });
window.updateTerminal = (text, reset, fontSize, online, latestLabel) => {
  latest.setAttribute('aria-label', latestLabel);
  latest.title = latestLabel;
  if (reset) terminal.reset();
  const needsFit = terminal.options.fontSize !== fontSize || online !== connected;
  if (terminal.options.fontSize !== fontSize) terminal.options.fontSize = fontSize;
  if (online && !connected) lastSize = '';
  connected = online;
  if (text) terminal.write(text, updateLatest);
  if (needsFit) scheduleFit();
};
