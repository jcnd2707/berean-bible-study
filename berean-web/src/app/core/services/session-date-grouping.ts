// Pure grouping logic for the study sessions list, kept separate from SessionsListComponent so it
// can be unit tested without the Angular test bed (see tests/session-date-grouping.test.js).
export type SessionDateGroupName = "Today" | "This week" | "Earlier";

export interface DateGroupable {
  updatedAt: string;
}

export interface SessionDateGroup<T> {
  group: SessionDateGroupName;
  items: T[];
}

/**
 * Groups items by how recently `updatedAt` falls relative to `now` (default: the current time).
 * "Today" is the same calendar day as `now`; "This week" is the six days before that; everything
 * else (including a date that fails to parse) is "Earlier". Empty groups are omitted, and group
 * order is always Today, This week, Earlier.
 */
export function groupByDate<T extends DateGroupable>(items: T[], now: Date = new Date()): SessionDateGroup<T>[] {
  const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const startOfWeek = startOfToday - 6 * 24 * 60 * 60 * 1000; // today plus the six days before it

  const today: T[] = [];
  const thisWeek: T[] = [];
  const earlier: T[] = [];

  for (const item of items) {
    const t = new Date(item.updatedAt).getTime();
    if (Number.isNaN(t) || t < startOfWeek) earlier.push(item);
    else if (t >= startOfToday) today.push(item);
    else thisWeek.push(item);
  }

  return [
    { group: "Today" as const, items: today },
    { group: "This week" as const, items: thisWeek },
    { group: "Earlier" as const, items: earlier },
  ].filter((g) => g.items.length > 0);
}
