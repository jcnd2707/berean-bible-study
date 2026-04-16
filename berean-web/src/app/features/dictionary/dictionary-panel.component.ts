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
import { WordSelectionService } from "../../core/services/word-selection.service";
import { NavigationStateService } from "../../core/services/navigation-state.service";
import { parseDefinition, ParsedDefinition } from "./definition-parser";
import { catchError } from "rxjs/operators";
import { EMPTY, forkJoin, of } from "rxjs";

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

  private readonly ALL_MODULES = ["strong", "bdb", "eastons"];
  private readonly STRONGS_MODULES = ["strong", "bdb"];

  private readonly _lookupEffect = effect(() => {
    const sel = this.wordSelection.selection();
    if (!sel) return;
    this.lastWord.set(sel.word);
    this.runLookup(sel.word, sel.strongs);
  });

  ngOnInit(): void {
    forkJoin(
      this.ALL_MODULES.map((id) =>
        this.dictService.getDetails(id).pipe(catchError(() => of(null))),
      ),
    ).subscribe((details) => {
      const tabs: DictTab[] = this.ALL_MODULES.map((id, i) => {
        const d = details[i] as DictionaryModuleDetails | null;
        return {
          moduleId: id,
          label: d?.abbreviation || id,
          isStrongs: d?.isStrongs ?? this.STRONGS_MODULES.includes(id),
        };
      });
      this.tabs.set(tabs);
      const eastons = tabs.find((t) => t.moduleId === "eastons");
      this.activeTabId.set(eastons?.moduleId ?? tabs[0]?.moduleId ?? "");
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
              strongs: strongs ?? null,
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
        this.nav.setActiveWord(null);
        this.error.set(
          `${t.label} requires a Strong's number. Switch to a translation with Strong's numbers, or use Easton's for a plain word lookup.`,
        );
        this.loading.set(false);
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
