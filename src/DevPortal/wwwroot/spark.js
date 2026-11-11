(() => {
  // Loopback answers in a few ms; without a floor every endpoint would look like a mountain range.
  const MIN_SCALE_MS = 50;
  const PAD = 3;

  const median = values => {
    const sorted = values.filter(v => typeof v === 'number').sort((a, b) => a - b);
    if (!sorted.length) return null;
    const mid = sorted.length >> 1;
    return sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
  };

  // Histories are aligned on their newest sample: probes of different endpoints run on the same ~30 s cadence.
  function medianSeries(histories) {
    const length = Math.max(0, ...histories.map(h => h.length));
    return Array.from({ length }, (_, i) => median(histories.map(h => h[h.length - length + i]).filter(v => v !== undefined)));
  }

  // Totals of several owners (a project's processes and containers): truncated to the shortest history so the
  // oldest points do not dip where an owner has not been sampled yet.
  function sumSeries(histories) {
    const filled = histories.filter(h => h.length);
    if (!filled.length) return [];
    const length = Math.min(...filled.map(h => h.length));
    return Array.from({ length }, (_, i) => filled.reduce((sum, h) => sum + h[h.length - length + i], 0));
  }

  // Samples are numbers (ms) or null (probe got no HTTP response). Returns runs of points to draw as lines and the x
  // of every gap, or null when there are fewer than two real samples to draw.
  function sparkShape(samples, width, height, minScale = MIN_SCALE_MS) {
    if (samples.filter(v => typeof v === 'number').length < 2) return null;
    const top = Math.max(minScale, ...samples.filter(v => v !== null)) || 1;
    const x = i => samples.length === 1 ? width / 2 : PAD + (i / (samples.length - 1)) * (width - 2 * PAD);
    const y = v => height - PAD - (v / top) * (height - 2 * PAD);
    const runs = [];
    const gaps = [];
    let run = [];
    samples.forEach((v, i) => {
      if (v === null) {
        if (run.length) runs.push(run);
        run = [];
        gaps.push(+x(i).toFixed(1));
      } else {
        run.push([+x(i).toFixed(1), +y(v).toFixed(1)]);
      }
    });
    if (run.length) runs.push(run);
    return { runs, gaps };
  }

  function sparkline(samples, width, height, label, minScale, tone) {
    const shape = sparkShape(samples, width, height, minScale);
    if (!shape) return null;
    const ns = 'http://www.w3.org/2000/svg';
    const make = (tag, attrs) => {
      const node = document.createElementNS(ns, tag);
      Object.entries(attrs).forEach(([k, v]) => node.setAttribute(k, v));
      return node;
    };
    const svg = make('svg', { class: tone ? `spark ${tone}` : 'spark', width, height, viewBox: `0 0 ${width} ${height}`, role: 'img', 'aria-label': label });
    for (const run of shape.runs)
      svg.append(run.length === 1
        ? make('circle', { class: 'spark-line', cx: run[0][0], cy: run[0][1], r: 1.5 })
        : make('polyline', { class: 'spark-line', points: run.map(p => p.join(',')).join(' ') }));
    for (const gx of shape.gaps)
      svg.append(make('line', { class: 'spark-gap', x1: gx, x2: gx, y1: PAD, y2: height - PAD }));
    return svg;
  }

  const api = { median, medianSeries, sumSeries, sparkShape, sparkline };
  globalThis.DevPortalSpark = api;
  if (typeof module !== 'undefined') module.exports = api;
})();
