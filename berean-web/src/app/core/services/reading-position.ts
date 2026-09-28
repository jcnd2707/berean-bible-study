export interface SavedReadingPosition {
  moduleId: string;
  book: string;
  chapter: number;
  verse: number | null;
}

interface RestoreModule {
  moduleId: string;
}

interface RestoreBook {
  abbreviation: string;
  chapterCount: number;
}

/**
 * Turns a saved `{moduleId, book, chapter, verse}` JSON string into a location to navigate to, or
 * null if it shouldn't restore anything. Pure so it can be unit tested without the DOM/localStorage.
 */
export function resolveRestoredPosition(
  saved: string | null,
  modules: RestoreModule[],
  books: RestoreBook[],
): SavedReadingPosition | null {
  if (!saved || modules.length === 0 || books.length === 0) return null;

  let parsed: unknown;
  try {
    parsed = JSON.parse(saved);
  } catch {
    return null;
  }
  if (!parsed || typeof parsed !== "object") return null;

  const { moduleId, book, chapter, verse } = parsed as Record<string, unknown>;
  if (typeof book !== "string" || typeof chapter !== "number") return null;

  const bookEntry = books.find((b) => b.abbreviation === book);
  if (!bookEntry) return null;

  const resolvedModuleId =
    typeof moduleId === "string" && modules.some((m) => m.moduleId === moduleId)
      ? moduleId
      : modules[0].moduleId;

  const clampedChapter = Math.max(1, Math.min(chapter, bookEntry.chapterCount));

  return {
    moduleId: resolvedModuleId,
    book,
    chapter: clampedChapter,
    verse: typeof verse === "number" ? verse : null,
  };
}
