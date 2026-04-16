import { Injectable, inject } from "@angular/core";
import { HttpClient, HttpParams } from "@angular/common/http";
import { Observable } from "rxjs";
import { environment } from "../../../environments/environment";

export interface DictionaryLookupResult {
  topic: string;
  definition: string;
}

export interface DictionaryModuleDetails {
  title: string;
  abbreviation: string;
  description?: string;
  isStrongs: boolean;
  rightToLeft: boolean;
}

@Injectable({ providedIn: "root" })
export class DictionaryService {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  getDetails(moduleId: string): Observable<DictionaryModuleDetails> {
    return this.http.get<DictionaryModuleDetails>(
      `${this.base}/api/dictionary/${moduleId}/details`,
    );
  }

  lookupWord(
    moduleId: string,
    word: string,
  ): Observable<DictionaryLookupResult> {
    const params = new HttpParams().set("word", word);
    return this.http.get<DictionaryLookupResult>(
      `${this.base}/api/dictionary/${moduleId}/lookup`,
      { params },
    );
  }

  lookupStrongs(
    moduleId: string,
    strongs: string,
  ): Observable<DictionaryLookupResult> {
    const params = new HttpParams().set("strongs", strongs);
    return this.http.get<DictionaryLookupResult>(
      `${this.base}/api/dictionary/${moduleId}/lookup`,
      { params },
    );
  }

  search(
    moduleId: string,
    q: string,
    limit = 20,
  ): Observable<DictionaryLookupResult[]> {
    const params = new HttpParams().set("q", q).set("limit", limit);
    return this.http.get<DictionaryLookupResult[]>(
      `${this.base}/api/dictionary/${moduleId}/search`,
      { params },
    );
  }
}
