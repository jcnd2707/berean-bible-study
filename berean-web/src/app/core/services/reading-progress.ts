/**
 * Pure helpers for chapter read-tracking. Progress is canonical — keyed by the 1-66 book number
 * and chapter, never by a translation's book abbreviation — so a chapter read in the KJV also
 * shows as read in the ASV (CHAPTER_PROGRESS_PLAN.md D1). No Angular imports, so it bundles and
 * tests the same way as the other pure modules.
 */

/** What the progress data is queried by: anything that can say whether a "book.chapter" key is in it. */
export interface ReadChapterLookup {
  has(key: string): boolean;
}

export function chapterKey(book: number, chapter: number): string {
  return `${book}.${chapter}`;
}

export function readChapterCount(read: ReadChapterLookup, book: number, chapterCount: number): number {
  let count = 0;
  for (let chapter = 1; chapter <= chapterCount; chapter++) {
    if (read.has(chapterKey(book, chapter))) count++;
  }
  return count;
}

/** True once every chapter of the book is marked. A book with no chapters is never complete. */
export function isBookComplete(read: ReadChapterLookup, book: number, chapterCount: number): boolean {
  return chapterCount > 0 && readChapterCount(read, book, chapterCount) === chapterCount;
}

/**
 * "Sep 28" for a date this year, "Sep 28, 2025" for any other, so an old mark isn't mistaken for
 * a recent one. Returns an empty string for something that isn't a date.
 */
export function formatReadDate(iso: string, now: Date = new Date(), locale?: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";

  const sameYear = date.getFullYear() === now.getFullYear();
  return date.toLocaleDateString(locale, {
    month: "short",
    day: "numeric",
    ...(sameYear ? {} : { year: "numeric" }),
  });
}
