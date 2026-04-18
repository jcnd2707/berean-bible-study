import { Injectable, inject } from "@angular/core";
import { HttpClient, HttpParams } from "@angular/common/http";
import { Observable } from "rxjs";
import { environment } from "../../../environments/environment";
import {
  BookSummary,
  BookMeta,
  BookChapter,
  BookChapterContent,
} from "../models";

@Injectable({ providedIn: "root" })
export class BooksService {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  /** List all available books (catalog) */
  getAll(): Observable<BookSummary[]> {
    return this.http.get<BookSummary[]>(`${this.base}/api/resources/books`);
  }

  /** Full metadata for one book */
  getMeta(moduleId: string): Observable<BookMeta> {
    return this.http.get<BookMeta>(`${this.base}/api/books/${moduleId}`);
  }

  /** Ordered chapter list */
  getChapters(moduleId: string): Observable<BookChapter[]> {
    return this.http.get<BookChapter[]>(
      `${this.base}/api/books/${moduleId}/chapters`,
    );
  }

  /** Single chapter with all paragraphs */
  getChapter(
    moduleId: string,
    chapterId: number,
  ): Observable<BookChapterContent> {
    return this.http.get<BookChapterContent>(
      `${this.base}/api/books/${moduleId}/chapters/${chapterId}`,
    );
  }

  /** Full-text search within a book */
  search(moduleId: string, q: string): Observable<BookChapterContent[]> {
    const params = new HttpParams().set("q", q);
    return this.http.get<BookChapterContent[]>(
      `${this.base}/api/books/${moduleId}/search`,
      { params },
    );
  }
}
