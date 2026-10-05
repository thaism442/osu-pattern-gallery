const COMBO = ["#ff66aa", "#66ccff", "#ffcc55", "#88ee88"];
function preview(p) {
  const r = 32;
  let svg = `<svg class="pv" viewBox="0 0 512 384"><rect x="2" y="2" width="508" height="380" fill="none" stroke="#fff" stroke-opacity=".07" stroke-width="3"/>`;
  const objs = (p.objs || []).map(o => ({ ...o }));
  let combo = -1, num = 0;
  objs.forEach((o, i) => { if (i === 0 || o.nc || o.sp) { combo++; num = 0; } o.num = ++num; o.col = COMBO[combo % COMBO.length]; });

  // dashed lines show the order (flow)
  for (let i = 1; i < objs.length; i++) {
    const a = objs[i - 1], b = objs[i];
    if (a.sp || b.sp) continue;
    const e = a.path && a.path.length ? a.path[a.path.length - 1] : [a.x, a.y];
    svg += `<line x1="${e[0]}" y1="${e[1]}" x2="${b.x}" y2="${b.y}" stroke="#fff" stroke-opacity=".2" stroke-width="3" stroke-dasharray="8 8"/>`;
  }
  // back to front so the first object is on top
  for (let i = objs.length - 1; i >= 0; i--) {
    const o = objs[i];
    if (o.sp) {
      svg += `<circle cx="256" cy="192" r="150" fill="none" stroke="#fff" stroke-opacity=".5" stroke-width="6"/><circle cx="256" cy="192" r="10" fill="#fff"/>`;
      continue;
    }
    if (o.path && o.path.length > 1) {
      const pts = o.path.map(q => q[0] + "," + q[1]).join(" ");
      svg += `<polyline points="${pts}" fill="none" stroke="#fff" stroke-width="${r * 2}" stroke-linecap="round" stroke-linejoin="round"/>`;
      svg += `<polyline points="${pts}" fill="none" stroke="${o.col}" stroke-opacity=".38" stroke-width="${r * 2 - 9}" stroke-linecap="round" stroke-linejoin="round"/>`;
      const e = o.path[o.path.length - 1];
      svg += `<circle cx="${e[0]}" cy="${e[1]}" r="${r - 5}" fill="none" stroke="#fff" stroke-width="4"/>`;
    }
    svg += `<circle cx="${o.x}" cy="${o.y}" r="${r}" fill="${o.col}" stroke="#fff" stroke-width="5"/>`;
    svg += `<text x="${o.x}" y="${o.y + 12}" text-anchor="middle" font-size="34" font-weight="800" fill="#fff" font-family="Exo 2,Segoe UI">${o.num}</text>`;
  }
  return svg + "</svg>";
}

