export interface PersonDto {
  qid: string;
  name: string;
  isDead: boolean;
  birthDate: string | null;
  deathDate: string | null;
  age: number | null;
  sitelinks: number;
  occupations: string[];
  wikidataUrl: string;
}

export interface DidYouMeanCandidate {
  qid: string;
  name: string;
  isDead: boolean;
  birthDate: string | null;
  deathDate: string | null;
  sitelinks: number;
  occupations: string[];
}

export interface SearchResponse {
  query: string;
  found: boolean;
  person: PersonDto | null;
  didYouMean: DidYouMeanCandidate[];
  queryTimeMs: number;
}

export interface ListResponse {
  count: number;
  queryTimeMs: number;
  items: PersonDto[];
}
