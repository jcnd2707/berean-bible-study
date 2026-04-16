import { Injectable, inject } from "@angular/core";
import { HttpClient, HttpParams } from "@angular/common/http";
import { Observable } from "rxjs";
import { environment } from "../../../environments/environment";
import {
  BookEntry,
  ChapterResponse,
  VerseResponse,
  SearchResult,
  SearchParams,
} from "../models";

@Injectable({ providedIn: "root" })
export class BibleService {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  getBooks(moduleId: string, lang = "en"): Observable<BookEntry[]> {
    const params = new HttpParams().set("lang", lang);
    return this.http.get<BookEntry[]>(
      `${this.base}/api/bible/${moduleId}/books`,
      { params },
    );
  }

  getChapter(
    moduleId: string,
    book: string,
    chapter: number,
    lang = "en",
  ): Observable<ChapterResponse> {
    const params = new HttpParams().set("lang", lang);
    return this.http.get<ChapterResponse>(
      `${this.base}/api/bible/${moduleId}/${book}/${chapter}`,
      { params },
    );
  }

  getVerse(
    moduleId: string,
    book: string,
    chapter: number,
    verse: number,
    lang = "en",
  ): Observable<VerseResponse> {
    const params = new HttpParams().set("lang", lang);
    return this.http.get<VerseResponse>(
      `${this.base}/api/bible/${moduleId}/${book}/${chapter}/${verse}`,
      { params },
    );
  }

  search(moduleId: string, p: SearchParams): Observable<SearchResult[]> {
    let params = new HttpParams().set("q", p.q);
    if (p.limit) params = params.set("limit", p.limit);
    if (p.testament && p.testament !== "both")
      params = params.set("testament", p.testament);
    if (p.book) params = params.set("book", p.book);
    return this.http.get<SearchResult[]>(
      `${this.base}/api/bible/${moduleId}/search`,
      { params },
    );
  }
}
