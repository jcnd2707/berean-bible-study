import { Injectable, inject } from "@angular/core";
import { HttpClient } from "@angular/common/http";
import { Observable } from "rxjs";
import { environment } from "../../../environments/environment";
import { BibleModule, CommentaryModule, DictionaryModule } from "../models";

@Injectable({ providedIn: "root" })
export class ResourcesService {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  getBibles(): Observable<BibleModule[]> {
    return this.http.get<BibleModule[]>(`${this.base}/api/resources/bibles`);
  }

  getCommentaries(): Observable<CommentaryModule[]> {
    return this.http.get<CommentaryModule[]>(
      `${this.base}/api/resources/commentaries`,
    );
  }

  getDictionaries(): Observable<DictionaryModule[]> {
    return this.http.get<DictionaryModule[]>(
      `${this.base}/api/resources/dictionaries`,
    );
  }

  getLexicons(): Observable<DictionaryModule[]> {
    return this.http.get<DictionaryModule[]>(
      `${this.base}/api/resources/lexicons`,
    );
  }

  getBibleDetails(
    moduleId: string,
  ): Observable<import("../models").BibleModuleDetails> {
    return this.http.get<import("../models").BibleModuleDetails>(
      `${this.base}/api/bible/${moduleId}`,
    );
  }
}
