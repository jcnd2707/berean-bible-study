import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { CommentaryResponse } from '../models';

@Injectable({ providedIn: 'root' })
export class CommentaryService {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  getChapter(
    moduleId: string,
    book: string,
    chapter: number,
    lang = 'en'
  ): Observable<CommentaryResponse> {
    const params = new HttpParams().set('lang', lang);
    return this.http.get<CommentaryResponse>(
      `${this.base}/api/commentary/${moduleId}/${book}/${chapter}`,
      { params }
    );
  }

  getVerse(
    moduleId: string,
    book: string,
    chapter: number,
    verse: number,
    lang = 'en'
  ): Observable<CommentaryResponse> {
    const params = new HttpParams().set('lang', lang);
    return this.http.get<CommentaryResponse>(
      `${this.base}/api/commentary/${moduleId}/${book}/${chapter}/${verse}`,
      { params }
    );
  }
}
