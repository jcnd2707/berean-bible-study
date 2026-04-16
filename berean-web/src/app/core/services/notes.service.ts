import { Injectable, inject } from "@angular/core";
import { HttpClient } from "@angular/common/http";
import { Observable } from "rxjs";
import { environment } from "../../../environments/environment";
import { Note, UpsertNoteRequest } from "../models";

@Injectable({ providedIn: "root" })
export class NotesService {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  getAll(): Observable<Note[]> {
    return this.http.get<Note[]>(`${this.base}/api/notes`);
  }

  getNote(reference: string): Observable<Note> {
    return this.http.get<Note>(`${this.base}/api/notes/${reference}`);
  }

  upsert(reference: string, text: string): Observable<Note> {
    const body: UpsertNoteRequest = { text };
    return this.http.post<Note>(`${this.base}/api/notes/${reference}`, body);
  }

  delete(reference: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/api/notes/${reference}`);
  }

  /** Formats a BibleLocation into the reference string expected by the API e.g. "Gen.1.1" */
  static toReference(
    book: string,
    chapter: number,
    verse: number | null,
  ): string {
    if (verse) return `${book}.${chapter}.${verse}`;
    return `${book}.${chapter}`;
  }
}
