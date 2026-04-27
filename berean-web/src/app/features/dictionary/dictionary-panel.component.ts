import {
  Component,
  OnInit,
  inject,
  signal,
  computed,
  effect,
} from "@angular/core";
import { CommonModule } from "@angular/common";
import {
  DictionaryService,
  DictionaryLookupResult,
  DictionaryModuleDetails,
} from "../../core/services/dictionary.service";
import { ResourcesService } from "../../core/services/resources.service";
import { WordSelectionService } from "../../core/services/word-selection.service";
import { NavigationStateService } from "../../core/services/navigation-state.service";
import { parseDefinition, ParsedDefinition } from "./definition-parser";
import { catchError } from "rxjs/operators";
import { forkJoin, of, EMPTY } from "rxjs";

interface DictTab {
  moduleId: string;
  label: string;
  isStrongs: boolean;
}

@Component({
  selector: "app-dictionary-panel",
  standalone: true,
  imports: [CommonModule],
  templateUrl: "./dictionary-panel.component.html",
  styleUrl: "./dictionary-panel.component.scss",
})
export class DictionaryPanelComponent implements OnInit {
  private readonly dictService = inject(DictionaryService);
  private readonly resourcesService = inject(ResourcesService);
  readonly wordSelection = inject(WordSelectionService);
  private readonly nav = inject(NavigationStateService);

  readonly tabs = signal<DictTab[]>([]);
  readonly activeTabId = signal<string>("");
  readonly rawResult = signal<DictionaryLookupResult | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly lastWord = signal<string>("");

  readonly parsed = computed<ParsedDefinition | null>(() => {
    const r = this.rawResult();
    if (!r) return null;
    return parseDefinition(r.topic, r.definition);
  });

  private readonly _lookupEffect = effect(() => {
    const sel = this.wordSelection.selection();
    if (!sel) return;
    this.lastWord.set(sel.word);
    this.runLookup(sel.word, sel.strongs);
  });

  ngOnInit(): void {
    // Load all dictionaries and lexicons from the API
    forkJoin({
      dicts: this.resourcesService
        .getDictionaries()
        .pipe(catchError(() => of([]))),
      lexicons: this.resourcesService
        .getLexicons()
        .pipe(catchError(() => of([]))),
    }).subscribe(({ dicts, lexicons }) => {
      const allModules = [...dicts, ...lexicons];
      if (allModules.length === 0) return;

      // Fetch details for each module to get isStrongs
      forkJoin(
        allModules.map((m) =>
          this.dictService
            .getDetails(m.moduleId)
            .pipe(catchError(() => of(null))),
        ),
      ).subscribe((details) => {
        const tabs: DictTab[] = allModules.map((m, i) => {
          const d = details[i] as DictionaryModuleDetails | null;
          return {
            moduleId: m.moduleId,
            label: d?.abbreviation || m.moduleId,
            isStrongs: d?.isStrongs ?? false,
          };
        });
        this.tabs.set(tabs);

        // Default to first plain-word module, fallback to first tab
        const plainFirst = tabs.find((t) => !t.isStrongs);
        this.activeTabId.set(plainFirst?.moduleId ?? tabs[0]?.moduleId ?? "");
      });
    });
  }

  selectTab(tab: DictTab): void {
    this.activeTabId.set(tab.moduleId);
    const word = this.lastWord();
    if (!word) return;
    const strongs = this.wordSelection.selection()?.strongs ?? null;
    this.runLookup(word, strongs, tab);
  }

  private activeTab(): DictTab | undefined {
    return this.tabs().find((t) => t.moduleId === this.activeTabId());
  }

  private runLookup(word: string, strongs: string | null, tab?: DictTab): void {
    const t = tab ?? this.activeTab();
    if (!t) return;

    this.loading.set(true);
    this.error.set(null);
    this.rawResult.set(null);

    if (t.isStrongs) {
      if (strongs) {
        this.dictService.lookupStrongs(t.moduleId, strongs).subscribe({
          next: (res) => {
            this.rawResult.set(res);
            this.nav.setActiveWord({
              word: res.topic,
              strongs,
              definition: res.definition.slice(0, 400),
              source: `${t.label} ${strongs}`,
            });
            this.loading.set(false);
          },
          error: () => {
            this.error.set(`${strongs} not found in ${t.label}.`);
            this.loading.set(false);
          },
        });
      } else {
        // No Strong's number in the verse — search the lexicon by word as fallback
        this.dictService
          .search(t.moduleId, word, 1)
          .pipe(catchError(() => of([] as DictionaryLookupResult[])))
          .subscribe({
            next: (results) => {
              if (results.length > 0) {
                this.rawResult.set(results[0]);
                this.nav.setActiveWord({
                  word: results[0].topic,
                  strongs: null,
                  definition: results[0].definition.slice(0, 400),
                  source: t.label,
                });
              } else {
                this.nav.setActiveWord(null);
                this.error.set(
                  `"${word}" not found in ${t.label}. For full lexicon access use a Strong's-tagged translation.`,
                );
              }
              this.loading.set(false);
            },
          });
      }
    } else {
      this.dictService
        .lookupWord(t.moduleId, word)
        .pipe(
          catchError(() =>
            this.dictService
              .search(t.moduleId, word, 1)
              .pipe(catchError(() => of(null))),
          ),
        )
        .subscribe({
          next: (res) => {
            if (!res) {
              this.error.set(`"${word}" not found in ${t.label}.`);
            } else if (Array.isArray(res)) {
              if (res.length > 0) {
                this.rawResult.set(res[0]);
                this.nav.setActiveWord({
                  word: res[0].topic,
                  strongs: null,
                  definition: res[0].definition.slice(0, 400),
                  source: t.label,
                });
              } else {
                this.error.set(`"${word}" not found in ${t.label}.`);
              }
            } else {
              const r = res as DictionaryLookupResult;
              this.rawResult.set(r);
              this.nav.setActiveWord({
                word: r.topic,
                strongs: null,
                definition: r.definition.slice(0, 400),
                source: t.label,
              });
            }
            this.loading.set(false);
          },
          error: () => {
            this.error.set(`"${word}" not found.`);
            this.loading.set(false);
          },
        });
    }
  }
}
