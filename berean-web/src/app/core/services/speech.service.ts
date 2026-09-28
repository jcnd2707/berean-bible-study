import { Injectable } from "@angular/core";

export interface SpeakOptions {
  lang: string;
  rate?: number;
}

export interface SpeakResult {
  ok: boolean;
  message?: string;
}

const LANGUAGE_NAMES: Record<string, string> = {
  en: "English",
  es: "Spanish",
  fr: "French",
  de: "German",
  pt: "Portuguese",
  it: "Italian",
  el: "Greek",
  he: "Hebrew",
};

function languageName(lang: string): string {
  return LANGUAGE_NAMES[lang.split("-")[0].toLowerCase()] ?? lang;
}

/**
 * Thin wrapper around the Web Speech API (`speechSynthesis`). Offline, free, and available in
 * every browser Berean runs in, including the Android PWA and iOS Safari.
 */
@Injectable({ providedIn: "root" })
export class SpeechService {
  private voicesPromise: Promise<SpeechSynthesisVoice[]> | null = null;
  // Remembered per language for the session, not persisted — a fresh page load re-picks.
  private readonly chosenVoice = new Map<string, SpeechSynthesisVoice>();

  private get synth(): SpeechSynthesis | null {
    return typeof speechSynthesis === "undefined" ? null : speechSynthesis;
  }

  /**
   * `getVoices()` is empty until `voiceschanged` fires in Chrome. Resolves once voices are ready,
   * or after a short timeout so a browser that never fires the event doesn't hang forever.
   */
  private loadVoices(): Promise<SpeechSynthesisVoice[]> {
    if (this.voicesPromise) return this.voicesPromise;

    const synth = this.synth;
    if (!synth) {
      this.voicesPromise = Promise.resolve([]);
      return this.voicesPromise;
    }

    const existing = synth.getVoices();
    if (existing.length > 0) {
      this.voicesPromise = Promise.resolve(existing);
      return this.voicesPromise;
    }

    this.voicesPromise = new Promise((resolve) => {
      let settled = false;
      const finish = (voices: SpeechSynthesisVoice[]) => {
        if (settled) return;
        settled = true;
        synth.removeEventListener("voiceschanged", onChange);
        resolve(voices);
      };
      const onChange = () => finish(synth.getVoices());
      synth.addEventListener("voiceschanged", onChange);
      setTimeout(() => finish(synth.getVoices()), 1000);
    });
    return this.voicesPromise;
  }

  private async pickVoice(lang: string): Promise<SpeechSynthesisVoice | null> {
    const cached = this.chosenVoice.get(lang);
    if (cached) return cached;

    const voices = await this.loadVoices();
    const base = lang.split("-")[0].toLowerCase();
    const matching = voices.filter((v) => v.lang.toLowerCase().startsWith(base));
    if (matching.length === 0) return null;

    // Prefer the variant the browser is set to (e.g. en-US over en-GB for a US browser).
    const preferred = (navigator.languages ?? [navigator.language]).map((l) => l.toLowerCase());
    const preferredVariant = matching.find((v) => preferred.includes(v.lang.toLowerCase()));

    const pool = preferredVariant
      ? matching.filter((v) => v.lang.toLowerCase() === preferredVariant.lang.toLowerCase())
      : matching;
    const chosen = pool.find((v) => v.localService) ?? pool[0];

    this.chosenVoice.set(lang, chosen);
    return chosen;
  }

  /** Whether this device has a voice for the given language (after voices have loaded). */
  async hasVoice(lang: string): Promise<boolean> {
    return (await this.pickVoice(lang)) !== null;
  }

  /**
   * Cancels anything already playing, then speaks `text` in `lang`. Every call here is expected
   * to originate from a click or tap — iOS only allows speech synthesis to start from a user
   * gesture, and voices are normally already cached by the time this runs, so the `await` below
   * stays inside that gesture's activation window in practice.
   */
  async speak(text: string, opts: SpeakOptions): Promise<SpeakResult> {
    const synth = this.synth;
    if (!synth) {
      return { ok: false, message: "Speech isn't supported on this device." };
    }

    synth.cancel();

    const voice = await this.pickVoice(opts.lang);
    if (!voice) {
      return {
        ok: false,
        message: `No ${languageName(opts.lang)} voice on this device (add one in your system's text-to-speech settings).`,
      };
    }

    const utterance = new SpeechSynthesisUtterance(text);
    utterance.voice = voice;
    utterance.lang = voice.lang;
    utterance.rate = opts.rate ?? 1;
    synth.speak(utterance);
    return { ok: true };
  }
}
