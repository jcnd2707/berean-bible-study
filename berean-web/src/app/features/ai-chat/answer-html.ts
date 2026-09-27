import type { ChatSource } from "../../core/services/agent-hub.service";

/**
 * Renders an answer as HTML: a small, safe subset of markdown (headings, bold, italics, code,
 * lists, rules) plus the [S1] / [A1] citations as clickable chips.
 *
 * Everything from the model is HTML-escaped first, and only the tags produced here are added, so
 * the result is safe to bind with [innerHTML] (trusted). Chips carry the source id in
 * data-cite; the component finds them with event delegation.
 */
export function renderAnswerHtml(
  text: string,
  sources: ChatSource[] | undefined,
  chipLabel: (s: ChatSource) => string,
  isOpenable: (s: ChatSource) => boolean,
): string {
  const lines = text.replace(/\r\n/g, "\n").split("\n");
  const html: string[] = [];
  let lastWasGap = true; // no leading gap

  for (const raw of lines) {
    const line = escapeHtml(raw);

    if (raw.trim() === "") {
      if (!lastWasGap) html.push('<div class="md-gap"></div>');
      lastWasGap = true;
      continue;
    }
    lastWasGap = false;

    const heading = /^\s{0,3}(#{1,6})\s+(.*)$/.exec(line);
    if (heading) {
      const level = Math.min(heading[1].length, 3);
      html.push(`<div class="md-h md-h${level}">${inline(heading[2])}</div>`);
      continue;
    }

    if (/^\s*([-*_])\1{2,}\s*$/.test(raw)) {
      html.push("<hr>");
      continue;
    }

    const bullet = /^\s*[-*•]\s+(.*)$/.exec(line);
    if (bullet) {
      html.push(`<div class="md-li">${inline(bullet[1])}</div>`);
      continue;
    }

    html.push(`<div class="md-p">${inline(line)}</div>`);
  }

  return withChips(html.join(""), sources, chipLabel, isOpenable);
}

export function escapeHtml(s: string): string {
  return s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

/** Bold, italics and code within one (already escaped) line. */
function inline(s: string): string {
  return s
    .replace(/`([^`]+)`/g, "<code>$1</code>")
    .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
    .replace(/(^|[^*\w])\*(?!\s)([^*]+?)(?<!\s)\*(?!\*)/g, "$1<em>$2</em>");
}

/** Turns [S1] into a chip for every source the server sent; other ids stay as text. */
function withChips(
  html: string,
  sources: ChatSource[] | undefined,
  chipLabel: (s: ChatSource) => string,
  isOpenable: (s: ChatSource) => boolean,
): string {
  if (!sources?.length) return html;

  return html.replace(/\[([SA]\d+)\]/g, (whole, id: string) => {
    const source = sources.find((s) => s.id === id);
    if (!source) return whole;

    const classes = ["cite"];
    if (source.tradition === "Adventist") classes.push("cite--adv");
    if (!isOpenable(source)) classes.push("cite--static");

    return (
      `<button type="button" class="${classes.join(" ")}" data-cite="${escapeHtml(id)}" ` +
      `title="${escapeHtml(source.label)}">${escapeHtml(chipLabel(source))}</button>`
    );
  });
}
