import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { CrossReferencesResponse } from '../models';

@Injectable({ providedIn: 'root' })
export class CrossReferencesService {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  get(
    book: string,
    chapter: number,
    verse: number,
    minVotes = 1,
    limit = 30
  ): Observable<CrossReferencesResponse> {
    const params = new HttpParams()
      .set('minVotes', minVotes)
      .set('limit', limit);
    return this.http.get<CrossReferencesResponse>(
      `${this.base}/api/crossreferences/${book}/${chapter}/${verse}`,
      { params }
    );
  }
}
