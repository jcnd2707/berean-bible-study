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

// ── Whole-Bible progress ────────────────────────────────────────────────────────────────────

/** The part of a book entry progress needs; the sidebar's BookEntry satisfies it. */
export interface ProgressBook {
  number: number; // canonical 1-66; 1-39 Old Testament, 40-66 New
  name: string;
  abbreviation: string;
  chapterCount: number;
}

export interface BookProgress extends ProgressBook {
  read: number;
  complete: boolean;
}

export interface GroupProgress {
  read: number;
  total: number;
  percent: number;
  booksComplete: number;
  bookCount: number;
}

export interface ReadingProgress {
  totalRead: number;
  totalChapters: number;
  percent: number;
  ot: GroupProgress;
  nt: GroupProgress;
  books: BookProgress[];
}

const LAST_OT_BOOK = 39;

/** Whole-number percent, rounded down so 100 is only ever shown when everything is read. */
export function percentOf(read: number, total: number): number {
  return total > 0 ? Math.floor((read * 100) / total) : 0;
}

function summarise(books: BookProgress[]): GroupProgress {
  const read = books.reduce((n, b) => n + b.read, 0);
  const total = books.reduce((n, b) => n + b.chapterCount, 0);
  return {
    read,
    total,
    percent: percentOf(read, total),
    booksComplete: books.filter((b) => b.complete).length,
    bookCount: books.length,
  };
}

export function computeProgress(read: ReadChapterLookup, books: ProgressBook[]): ReadingProgress {
  const perBook: BookProgress[] = books.map((b) => {
    const count = readChapterCount(read, b.number, b.chapterCount);
    return { ...b, read: count, complete: b.chapterCount > 0 && count === b.chapterCount };
  });

  const ot = summarise(perBook.filter((b) => b.number <= LAST_OT_BOOK));
  const nt = summarise(perBook.filter((b) => b.number > LAST_OT_BOOK));
  const totalRead = ot.read + nt.read;
  const totalChapters = ot.total + nt.total;

  return { totalRead, totalChapters, percent: percentOf(totalRead, totalChapters), ot, nt, books: perBook };
}

const MILESTONES = [10, 25, 50, 75, 90, 100];

export interface NextMilestone {
  percent: number;
  chaptersToGo: number;
}

/** The next whole-Bible percentage worth mentioning and how many chapters away it is; null once everything is read. */
export function nextMilestone(progress: Pick<ReadingProgress, "totalRead" | "totalChapters">): NextMilestone | null {
  const { totalRead, totalChapters } = progress;
  if (totalChapters <= 0) return null;

  for (const percent of MILESTONES) {
    const chaptersNeeded = Math.ceil((percent * totalChapters) / 100);
    if (totalRead < chaptersNeeded) return { percent, chaptersToGo: chaptersNeeded - totalRead };
  }
  return null;
}
