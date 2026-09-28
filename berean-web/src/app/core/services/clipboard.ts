import { Injectable } from "@angular/core";

/**
 * `navigator.clipboard` only exists on HTTPS or localhost pages. Berean is often opened over
 * plain `http://<pc-ip>` from a phone, so a hidden-textarea + `execCommand("copy")` fallback is
 * needed for that case.
 */
@Injectable({ providedIn: "root" })
export class ClipboardService {
  async copy(text: string): Promise<boolean> {
    if (window.isSecureContext && navigator.clipboard?.writeText) {
      try {
        await navigator.clipboard.writeText(text);
        return true;
      } catch {
        // fall through to the legacy fallback
      }
    }
    return this.copyWithExecCommand(text);
  }

  private copyWithExecCommand(text: string): boolean {
    const textarea = document.createElement("textarea");
    textarea.value = text;
    textarea.setAttribute("readonly", "");
    textarea.style.position = "fixed";
    textarea.style.left = "-9999px";
    document.body.appendChild(textarea);
    textarea.select();
    textarea.setSelectionRange(0, text.length);

    let ok = false;
    try {
      ok = document.execCommand("copy");
    } catch {
      ok = false;
    }
    document.body.removeChild(textarea);
    return ok;
  }
}
